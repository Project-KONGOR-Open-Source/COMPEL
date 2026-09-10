# Proxy Abuse Protection Design

**Status:** approved in conversation, not yet implemented.

**Goal:** give COMPEL's managed proxy the rate limiting and abuse scoring that the original Heroes Of Newerth proxy performs, so a client cannot flood a match server through it. Watermark validation, the other half of the original's protection, is explicitly deferred to a later increment.

**Authoritative source:** `HoN_Proxy`, a clone of `https://endergit.cloud/HoN/HoNProxy.git` with retail-tagged history. Its `HoN/branches/retail/Tool/HoNProxy/main.cpp` is the reference implementation. The sibling `Proxy Manager Executable And Source` folder is a redundant extract: its `proxymanager.py` is byte-identical to the repository's copy, and its `proxymanager.exe` is byte-identical to the one shipped in the `was` distribution.

## Why this is worth doing

COMPEL's proxy already relays correctly and issues the challenge the client requires. That was verified end to end: a real client played an eight-minute match through it with `Disconnects(0)`, and the match server logged the client's source as the proxy's upstream socket rather than the client's own port.

What it does not do is limit anything. The mechanism is not missing, it is switched off.

## Findings

### The rate limiter is the challenge quota

The original does not run a general-purpose rate limiter. Each challenge grants the client a packet quota, the client self-throttles against it, and the proxy enforces the same number server-side. Exceeding it costs violation score and the datagram is dropped (`main.cpp:659-680`).

| Original constant | Value | Effective rate |
| --- | --- | --- |
| `CHALLENGE_REFRESH_TIME` | 5 seconds | challenge changes every 5 seconds |
| `CHALLENGE_MAX_CTR` | 720 | 144 packets per second |
| `CHALLENGE_MAX_GAME_CMD_CTR` | 40 | 8 packets per second |
| `CHALLENGE_MAX_CTR_VOICE` | 50 | 10 packets per second |
| `MAX_CTR_UNAUTHENTICATED` | 100 | cap before the first challenge |

COMPEL currently advertises an unlimited quota and enforces nothing:

```csharp
ChallengeExpirySeconds             = 60;
ChallengeMaximumCounter            = ushort.MaxValue;   // 65535 against the original's 720
ChallengeMaximumGameCommandCounter = ushort.MaxValue;   // 65535 against the original's 40
```

### Violation score is a separate, decaying accumulator

Violations add weight to a per-source score, and a housekeeping pass drains every score at a fixed rate (`main.cpp:1184-1192`):

```cpp
decay = elapsed_milliseconds * ESTIMATED_PACKETS_PER_SECOND / 1000.0f;
warns = max(0, warns - decay)
```

A source at normal rates drains to zero; a source that misbehaves climbs toward the action threshold. The original's weights range from 30 to 200 per violation against a threshold of 4000, so a single anomaly never acts on its own.

### The original's enforcement is rejected

The original enforces with firewall bans (`FIREWALL_BAN_THREADPOOL_SIZE`) and a persisted hardware-identifier ban list (`permabanned_raw_hwids.txt`). COMPEL's existing `UDPProxyService` comment already records that this mechanism was removed as ineffective, and at the proxy layer there is only an address and a datagram, with no account to attribute abuse to. **Enforcement is therefore a local drop with no persisted state**: score per source, drop while over the threshold, forget when the session expires.

## Scope

In scope:

1. Advertising and enforcing the per-challenge packet quotas.
2. The pre-authentication packet cap.
3. The decaying violation score container and the action threshold.
4. The under-attack indicator.
5. Counters exposed through `/status`.

Explicitly deferred, recorded as a `TODO` in the code:

- Watermark validation, both the constant per-region watermark and the dynamic CRC32C one. It is the higher-value anti-cheat signal but carries the higher false-positive risk, and it needs a new `region` setting that has no COMPEL equivalent today.
- Packet-shape validation (minimum lengths, packet type, network command validity), which needs `game_data_protocol.h` ported.
- Duplicate detection.

## Design

### Quota derivation

The advertised quota and the enforced quota must be the same number, and both depend on how often the challenge is renewed. The original derives 144 packets per second from 720 over 5 seconds; COMPEL renews every 10 seconds, so the identical rate needs 1440.

Rather than hard-coding two numbers that can silently disagree, **the quota is derived from the rate and the renewal interval**:

```
quota = ratePerSecond * challengeRenewalInterval.TotalSeconds
```

The rates are the constants; the quota is computed. This makes it impossible for the advertised and enforced values to drift apart when the renewal interval is changed, which is the single most likely way to introduce a false positive here.

The rates match the original: 144 per second total, 8 per second for game commands, 10 per second for voice.

### Components

**`ViolationScoreContainer`** — the per-source score with its drain. Backed by the framework rather than hand-rolled:

| Concept | Implementation |
| --- | --- |
| Per-source partitioning, idle cleanup, thread safety | `PartitionedRateLimiter` keyed on the source endpoint |
| Container capacity | `ActionableThreshold` |
| Drain rate | `TokensPerPeriod` / `ReplenishmentPeriod`, from `EstimatedPacketsPerSecond` |
| Weighted violation | `AttemptAcquire(permitCount: weight)` |
| Over threshold | the acquisition fails |

`System.Threading.RateLimiting` is part of the ASP.NET Core shared framework for `net11.0`, so no package reference is added, and it is reflection-free so it remains Native AOT compatible. Only the synchronous `AttemptAcquire` is used, with a queue limit of zero: the datagram loop must never await on a limiter.

A token container is the exact dual of the original's accumulate-and-decay model — consuming capacity as score accrues, recovering as it drains — so the behaviour matches without reimplementing it.

**Per-challenge counters** — deliberately *not* a framework limiter. The quota is a protocol value COMPEL advertises inside the challenge packet, and the client resets its own counter when the challenge changes. Enforcement must therefore reset on COMPEL's renewal event. A windowed limiter owns its window internally, offers no reset, and would drift against renewals, so a client correctly respecting the quota it was handed could still be rejected. These are two integers per session (total and game command) plus a pre-authentication cap, held in the existing session record and reset by the renewal that already exists.

**Under-attack indicator** — a single counter incremented per violation, reset periodically, logged when it exceeds `UnderAttackThreshold`, and surfaced on `/status`.

**Decay and reset tick** — `UDPProxyService` already runs a periodic loop for challenge renewal. The counter resets hang off that. The score container drains itself through its replenishment period and needs no tick of COMPEL's own.

### Naming

The original's names are C-style abbreviations. New names use PascalCase and full words, with the original `#define` recorded in a trailing comment for traceability:

| Original | New |
| --- | --- |
| `BAN_THRESHOLD` | `ActionableThreshold` |
| `MAX_WARN_COUNT` | `MaximumViolationScore` |
| `ESTIMATED_PACKETS_PER_SECOND` | `EstimatedPacketsPerSecond` |
| `UNDER_ATTACK_THRESHOLD` | `UnderAttackThreshold` |
| `WARN_LIMIT` | `RateLimitViolationWeight` |
| `WARN_UNAUTHENTICATED` | `UnauthenticatedViolationWeight` |
| `WARN_DUPE` | `DuplicateViolationWeight` |
| `CHALLENGE_MAX_CTR` | `ChallengePacketsPerSecond` (a rate; the quota is derived) |
| `CHALLENGE_MAX_GAME_CMD_CTR` | `ChallengeGameCommandPacketsPerSecond` |
| `MAX_CTR_UNAUTHENTICATED` | `UnauthenticatedPacketQuota` |

`ActionableThreshold` is deliberately generic: the action is a drop today, and naming it after dropping would misname it if the policy ever changes. "Container" is used throughout in place of the original's bucket metaphor; the single unavoidable exception is the framework's own `TokenBucketRateLimiter` type name.

The weights for deferred checks are not defined yet, because defining unused constants for checks that do not exist would be dead code.

### Data flow

For each datagram arriving on a public port:

1. Look up or create the session, as today.
2. If the session has no challenge yet, increment its pre-authentication counter and compare it against `UnauthenticatedPacketQuota`, which is a total rather than a rate and is never replenished for the life of that unauthenticated state; over it, add `UnauthenticatedViolationWeight` and drop. COMPEL issues a challenge to every session on creation, so this state is normally momentary and the cap only matters for a source that sends without ever accepting a challenge.
3. Otherwise increment the session's packet counter (and, for a game command, its separate game command counter) and compare each against its derived quota; over either, add `RateLimitViolationWeight` and drop.
4. Ask the score container whether the source is within `ActionableThreshold`; if not, drop.
5. Relay upstream, exactly as today.

Return traffic from the game server is unchanged and unscored: it does not originate from a client.

### Error handling

Dropping is silent to the client by design, matching the original. Every drop increments a counter; the first drop for a source is logged, and subsequent drops for the same source are not, so a flood cannot itself become a log flood. The score container failing to acquire is a normal outcome, not an exception path.

### Enforcement is enabled from the start

Dropping is active on the first build rather than shipped observe-only, so it can be tested against a real match immediately. The safety net is therefore the test suite plus a live match check, not a staged rollout. This is a deliberate decision and the reason the score container's arithmetic is tested directly.

## Testing

The score container and the quota derivation are pure and get the majority of the tests:

- A source at the expected packet rate never reaches `ActionableThreshold`, over a simulated run long enough to cover several drain periods. This is the false-positive guard and the most important test in the set.
- A source above the rate crosses the threshold, and recovers after draining.
- Weighted violations accumulate as expected; a single violation of any weight never crosses the threshold alone.
- The derived quota equals rate multiplied by renewal interval, and changing the interval changes the quota consistently, so the advertised and enforced values cannot diverge.
- The pre-authentication cap is enforced separately from the authenticated quota.

Verification beyond the suite: a real match through the proxy on the local NEXUS setup, confirming `Disconnects(0)` and no drops recorded against a legitimate client.

## Risks

The material risk is a false positive: dropping a legitimate player mid-match, which presents as a network fault and is hard to attribute. Three things mitigate it — the quota is derived rather than duplicated, the rates match the original's proven values, and the drain-rate test asserts that normal traffic never approaches the threshold.

A second, smaller risk is that COMPEL's renewal interval (10 seconds) is twice the original's refresh (5 seconds), so a quota covers a longer window and a burst within it is tolerated where the original would have acted. This is accepted: the rate is what matters, and a longer window is more forgiving rather than less.
