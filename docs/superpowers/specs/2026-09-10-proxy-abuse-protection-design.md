# Proxy Abuse Protection Design

**Status:** design agreed in conversation; corrected after verifying every load-bearing claim against the reference implementation. Not yet implemented.

**Goal:** give COMPEL's managed proxy the packet validation, rate limiting and abuse scoring that the original Heroes Of Newerth proxy performs, so a client cannot flood or malform its way through it to a match server. Watermark validation is explicitly deferred.

**Authoritative source:** `HoN_Proxy`, a clone of `https://endergit.cloud/HoN/HoNProxy.git` with retail-tagged history; `HoN/branches/retail/Tool/HoNProxy/main.cpp` is the reference. The sibling `Proxy Manager Executable And Source` folder is a redundant extract: its `proxymanager.py` is byte-identical to the repository's copy, and its `proxymanager.exe` is byte-identical to the one shipped in the `was` distribution.

## Why this is worth doing

COMPEL's proxy relays correctly and issues the challenge the client requires. That is verified end to end: a real client played an eight-minute match through it with `Disconnects(0)`, and the match server logged the client's source as the proxy's upstream socket rather than the client's own port.

What it does not do is validate or limit anything. It reads nothing from an incoming datagram and forwards it verbatim. The quota mechanism is not missing — COMPEL already advertises the fields — it is switched off.

## Verified protocol facts

Everything in this section was read from the reference implementation, not inferred.

### The client supplies the counter

The rate limit is not a proxy-side counter. The client writes a counter into every datagram and the proxy validates it. Incoming client packet layout (`main.cpp:631-634`):

| Offset | Size | Field |
| --- | --- | --- |
| `0`–`11` | 12 | constant watermark (region-specific) |
| `12`–`15` | 4 | dynamic watermark |
| `20`–`23` | 4 | account identifier |
| `24`–`27` | 4 | short hardware identifier |
| `28`–`31` | 4 | challenge, echoed back from the challenge packet |
| `32`–`33` | 2 | **counter**, incremented by the client per packet |

So enforcement is a comparison against the maximum COMPEL itself advertised, and it is stateless per datagram.

### Minimum lengths are mandatory before reading those fields

`main.cpp:571-576` drops short datagrams before touching any field:

```cpp
// 40 bytes Watermark + 2 bytes connection + 1 bytes packet type.
if ((!game && length < 41) || (game && (length < 43 …
```

Voice requires at least 41 bytes, game at least 43. Reading the counter at offset 32 requires at least 34, so this guard is a **prerequisite for the counter read, not an optional extra**. COMPEL has no length guard today; adding the counter read without one would be an out-of-bounds read triggerable by any short datagram.

### The quotas are per challenge, and differ by kind

| Original constant | Value | Effective rate |
| --- | --- | --- |
| `CHALLENGE_REFRESH_TIME` | 5 seconds | how often the challenge changes |
| `CHALLENGE_MAX_CTR` | 720 | 144 packets per second (game) |
| `CHALLENGE_MAX_GAME_CMD_CTR` | 40 | 8 packets per second |
| `CHALLENGE_MAX_CTR_VOICE` | 50 | 10 packets per second (voice) |
| `MAX_CTR_UNAUTHENTICATED` | 100 | total before the first challenge, not a rate |

The advertised maximum differs by forwarder kind (`main.cpp:986`):

```cpp
initializeChallengePackage(game ? CHALLENGE_MAX_CTR : CHALLENGE_MAX_CTR_VOICE);
```

### The challenge packet layout already matches

COMPEL's `BuildChallengePacket` is byte-for-byte identical to the reference macro (`main.cpp:272-285`): `0xFF 0xFF` at 40–41, `0b01000000` at 42, challenge type at 43, timestamp at 44–47, expiry at 48–49, maximum counter at 50–51, maximum game command counter at 52–53, challenge value at 54–57, 58 bytes total. Nothing about the packet needs changing except the values.

Current COMPEL values, and why they disable the mechanism:

```csharp
ChallengeExpirySeconds             = 60;              // reference advertises 20
ChallengeMaximumCounter            = ushort.MaxValue; // reference advertises 720 (game) or 50 (voice)
ChallengeMaximumGameCommandCounter = ushort.MaxValue; // reference advertises 40
```

The longer expiry is a trade-off rather than a plain margin: with an unmatched challenge now refused, sixty seconds is a longer window in which a client can echo something this proxy no longer knows. Task 9's per-second repeat is what makes that safe, by re-issuing the current challenge often enough that a client rarely has to fall back on the full expiry.

### Violation score is a separate decaying accumulator

Every packet a source sends costs 1, unconditionally (`main.cpp:534`); violations add their weight on top; and a housekeeping pass drains the score in proportion to elapsed time (`main.cpp:1177-1193`):

```cpp
++warns;                                                          // every packet, before any violation weight
decay = elapsed_milliseconds * ESTIMATED_PACKETS_PER_SECOND / 1000.0f;
warns = max(0, warns - decay)                                     // only once elapsed_milliseconds > 900
                                                                  // warns is an unsigned int, so the RESULT is truncated
```

The per-packet cost is what gives the drain rate its meaning. The reference documents `ESTIMATED_PACKETS_PER_SECOND` as "we expect client to send less than this amount packets per second", so a source at or below 140 packets a second nets zero while one above it accumulates with no violation at all. Scoring violation weights alone would leave a well-behaved source permanently at zero and make the drain rate arbitrary.

Weights run from 30 to 200 against a threshold of 4000, so no single anomaly acts on its own.

**Two ceilings, not one.** `BAN_THRESHOLD` (4000) is where a source starts being acted upon; `MAX_WARN_COUNT` (20000) bounds how far above it the score may climb. Between them the reference keeps adding `WARN_BANNED` (10) per packet (`main.cpp:542-543`), deliberately — its comment says "in case the firewall rules fail for any reason". A source that keeps pushing therefore climbs to five times the threshold and needs roughly 114 seconds to decay back under it, against roughly 29 seconds for one that stops as soon as it is actioned. Collapsing the two ceilings onto one number loses that graduated persistence, which is what makes the score expensive for a persistent attacker and cheap for a client that misbehaves once.

One deliberate divergence: in the reference, `MAX_WARN_COUNT` bounds only the escalation, while the per-packet cost and the ordinary weights are added with no ceiling, so its count climbs until it wraps. COMPEL saturates every path at `MaximumViolationScore` instead. That keeps the arithmetic in range and bounds recovery to roughly two and a half minutes of drain, at the cost of a flood's penalty no longer growing with its duration — which is the right trade when the response is a local drop rather than a firewall ban.

**Why the drain rate does not act on a compliant client.** The reference's three rate constants are deliberately ordered, and the drain sits between them: the client's soft self-limit is 675 packets per challenge (135 a second, `main.cpp:684`), the drain is 140 a second, and the hard quota is 720 (144 a second, `main.cpp:674`). A client honouring its own soft limit therefore nets minus five a second and never accumulates. Only a client sending between 141 and 144 — above what it should send, below what is refused outright — drifts upward, which is the intent rather than a false positive.

### The original's enforcement is rejected

The original enforces with firewall bans and a persisted hardware-identifier ban list. COMPEL's existing comment already records that mechanism as removed for being ineffective, and at the proxy layer there is only an address and a datagram, with no account to attribute abuse to. **Enforcement is a local drop with no persisted state**: score per source, drop while over the threshold, forget when the session expires.

## Scope

In scope:

1. Minimum-length validation, per kind.
2. Reading the challenge and counter from incoming datagrams.
3. Accepting the current *and* previous challenge during renewal.
4. Advertising per-kind quotas and enforcing the packet quota, plus the pre-authentication cap. The game-command quota is advertised but not enforced, because the netcmd parsing it would need is deferred.
5. Duplicate counter detection.
6. The decaying violation score container and the actionable threshold.
7. The under-attack indicator, and counters on `/status`.

Deferred, recorded as a `TODO` in the code:

- Watermark validation, constant and dynamic. The higher-value anti-cheat signal, but the higher false-positive risk, and it needs a `region` setting COMPEL has no equivalent for.
- Packet type and network command validity, which need `game_data_protocol.h` ported.

## Design

### Renewal grace is the critical correctness detail

The quota is per challenge and the client resets its counter when it accepts a new one. COMPEL rotates challenges every ten seconds, so datagrams already in flight when a rotation happens carry the **previous** challenge and a counter that has not reset.

If only the current challenge were accepted, **every rotation would drop legitimate packets** — a false positive occurring every ten seconds for every player. Each session therefore retains the previous challenge and its counter state, and a datagram is validated against whichever of the two it echoes. Anything echoing neither is unauthenticated.

This is the single most important detail in the design and the easiest to omit.

### Quota derivation

The advertised maximum and the enforced maximum are the same number by construction, derived from a rate and the renewal interval:

```
quota = ratePerSecond * challengeRenewalInterval.TotalSeconds
```

The rates are the constants (144 per second game, 8 per second game command, 10 per second voice, matching the reference); the quota is computed. The reference reaches 144 per second as 720 over 5 seconds; COMPEL renews every 10 seconds, so the same rate is 1440. Hard-coding both numbers is how the advertised and enforced values silently diverge, so only the rate is written down.

### Components

**`ViolationScoreContainer`** — a per-source decaying score, implemented directly:

| Concept | Implementation |
| --- | --- |
| Per-source partitioning and thread safety | `ConcurrentDictionary<IPEndPoint, int>` keyed on the source endpoint |
| Accumulation | a saturating add, capped at `MaximumViolationScore` |
| Actioned | the score exceeds `ActionableThreshold` |
| Drain | `Replenish` subtracts `EstimatedPacketsPerSecond` for every elapsed second, flooring at zero |
| Idle cleanup | a source whose score reaches zero is forgotten |
| Clock | an injected `TimeProvider`, so the drain is testable without waiting on wall-clock time |

`System.Threading.RateLimiting` was the first choice and was rejected on evidence. A token container looks like the dual of accumulate-and-decay — capacity consumed as score accrues, recovered as it drains — but three properties of the framework's `TokenBucketRateLimiter` make it unable to express this model. All three were measured against `System.Threading.RateLimiting` 11.0.0.0 as loaded from `Microsoft.AspNetCore.App/11.0.0-preview.7`, the assembly a `net11.0` target actually resolves:

- **`AttemptAcquire` is all-or-nothing, where the reference always accumulates.** A charge larger than the remaining permits fails and consumes nothing. Measured on a complete token-bucket implementation of this component: after 400 duplicate-weight charges — a reference score of 12000, three times the threshold — the source was still within its allowance, parked indefinitely at 10 permits, because every further charge of 30 exceeded what was left. Any weight that does not divide the threshold exactly lets a source sit just below it and never be actioned, which defeats the component entirely.
- **The counter cannot exceed `TokenLimit`.** With the limit set to the threshold, the above-threshold escalation has nowhere to go: `MaximumViolationScore` becomes unrepresentable and the graduated persistence described above is lost.
- **The drain cannot be driven deterministically.** `TryReplenish` scales what it restores by real elapsed time — with a 1 ms period and 140 tokens per period, one call after 1000 ms restored the full 4000, not 140. `ReplenishmentPeriod` is rejected at `TimeSpan.Zero`, a one-tick period restores the whole limit even in a tight loop, and there is no `TimeProvider` anywhere on `TokenBucketRateLimiterOptions`. The only fixed-amount workaround is to reconstruct the limiter every second, which hand-rolls the drain regardless while adding object churn and a lost-charge race — and a fixed amount is the one behaviour the reference does not have, since its decay is proportional to elapsed time precisely so that a late housekeeping pass still decays by the right amount.

What remains once those are accounted for is not a rate limiter. It is a weighted score with linear decay, and the framework has no primitive for one — `TokenBucket`, `FixedWindow`, `SlidingWindow` and `Concurrency` all model request admission, not weighted scores. Implemented directly it is roughly forty lines, matches the reference exactly, holds no unmanaged resource, and is deterministic under test. `ConcurrentDictionary` and `TimeProvider` are both core and Native AOT-safe, so the component adds no trim risk of its own; the first Native AOT publish after this work lands is still checked for warnings.

One constraint follows and must hold:

- **No violation weight may exceed `ActionableThreshold`.** Not because a larger weight would throw — a saturating add cannot — but because a weight at or above the threshold would action a source on a single anomaly, which is the behaviour the weighting exists to avoid. The intended weights (at most 200 against a threshold of 4000) satisfy this comfortably, and a test asserts it so a later weight cannot violate it silently.

**`ClientPacketReader`** — a small static reader over a datagram span exposing the minimum length for a kind, the echoed challenge, and the counter. Pure, span-based, no allocation, and the natural home for the bounds checking. Every offset above lives here as a named constant rather than being scattered.

**Per-session challenge state** — the current and previous challenge, each with its counter ceiling and a set of the counters already seen, sized to the quota (a byte per counter, so under one and a half kilobytes at a 1440 quota; indexing beats bit-masking on the datagram path and the total is negligible). Held in the existing session record, rotated by the renewal that already exists.

**Under-attack indicator** — one counter incremented per violation, reset periodically, logged when it exceeds `UnderAttackThreshold`, surfaced on `/status`.

### Naming

PascalCase and full words, with the reference `#define` in a trailing comment for traceability:

| Reference | COMPEL |
| --- | --- |
| `BAN_THRESHOLD` | `ActionableThreshold` |
| `MAX_WARN_COUNT` | `MaximumViolationScore` |
| `ESTIMATED_PACKETS_PER_SECOND` | `EstimatedPacketsPerSecond` |
| `UNDER_ATTACK_THRESHOLD` | `UnderAttackThreshold` |
| `WARN_LIMIT` | `RateLimitViolationWeight` |
| `WARN_UNAUTHENTICATED` | `UnauthenticatedViolationWeight` |
| `WARN_DUPE` | `DuplicateViolationWeight` |
| `WARN_TOO_SHORT` | `TooShortViolationWeight` |
| `WARN_BANNED` | `ActionedViolationWeight` |
| `CHALLENGE_MAX_CTR` | `ChallengePacketsPerSecond` (a rate; quota derived) |
| `CHALLENGE_MAX_GAME_CMD_CTR` | `ChallengeGameCommandPacketsPerSecond` |
| `CHALLENGE_MAX_CTR_VOICE` | `ChallengeVoicePacketsPerSecond` |
| `MAX_CTR_UNAUTHENTICATED` | `UnauthenticatedPacketQuota` |

`ActionableThreshold` is deliberately generic: the action is a drop today, and naming it after dropping would misname it if the policy changed. `ActionedViolationWeight` follows the same reasoning as `ActionableThreshold`: the reference calls it `WARN_BANNED`, but COMPEL does not ban. "Container" replaces the reference's bucket metaphor throughout, with no exceptions now that no framework limiter type appears.

Weights for deferred checks are not defined, because constants for checks that do not exist are dead code.

### Data flow

For each datagram arriving on a public port:

1. **Score the packet.** Every datagram from a client costs 1 against its source, before any check runs (`main.cpp:534`). This is the cost the drain rate is calibrated against.
2. **Length guard.** Below the minimum for the kind (41 voice, 43 game), add `TooShortViolationWeight` and drop. Nothing else reads the datagram until this passes.
3. Look up or create the session, as today.
4. Read the echoed challenge and counter.
5. **Unauthenticated** (the challenge matches neither the current nor the previous): if the counter is at or above `UnauthenticatedPacketQuota`, add `UnauthenticatedViolationWeight` and drop. That quota is a total, never replenished while unauthenticated. COMPEL issues a challenge on session creation, so this state is normally momentary.
6. **Over quota**: the counter is at or above the derived maximum for the matched challenge and kind — add `RateLimitViolationWeight` and drop.
7. **Duplicate**: the counter has already been seen for that challenge — add `DuplicateViolationWeight` and drop. Otherwise record it.
8. **Actioned**: the source's score exceeds `ActionableThreshold` — add `ActionedViolationWeight` and drop, so a source that keeps pushing while actioned climbs towards `MaximumViolationScore` and stays actioned for longer.
9. Relay upstream, unchanged from today.

Return traffic from the match server is unchanged and unscored; it does not originate from a client.

### Error handling

Dropping is silent to the client, matching the reference. Every drop increments a counter; the first drop for a source is logged and subsequent ones are not, so a flood cannot become a log flood. A refused acquisition is a normal outcome, not an exception path. A malformed datagram can never cause an out-of-range read, because step 1 precedes every field access.

### Enforcement is enabled from the start

Dropping is active on the first build rather than staged, so it can be tested against a real match immediately. The safety net is therefore the test suite plus a live match check. This is a deliberate decision and the reason the arithmetic and the renewal grace are tested directly.

## Testing

The reader, the quota derivation and the score container are pure, and carry the majority of the tests:

- **Renewal grace:** a datagram echoing the previous challenge is accepted, and its counter is judged against that challenge's state. This guards the every-ten-seconds false positive and is the most important test in the set.
- **Drain rate:** a source at the expected packet rate never reaches `ActionableThreshold` across several drain periods. The second false-positive guard.
- A source above the rate crosses the threshold, and recovers after draining.
- Weighted violations accumulate as expected; no single violation of any weight crosses the threshold alone.
- Derived quota equals rate multiplied by renewal interval, and changing the interval changes the quota consistently, so advertised and enforced cannot diverge.
- The length guard rejects datagrams below the per-kind minimum, and a datagram one byte short of the minimum is rejected without any field being read.
- The pre-authentication cap is enforced independently of the authenticated quota.
- Duplicate counters within a challenge are rejected; the same counter under a new challenge is accepted.

Beyond the suite: a real match through the proxy on the local NEXUS setup, confirming `Disconnects(0)` and no drops recorded against a legitimate client.

## Risks

The material risk is a false positive: dropping a legitimate player mid-match presents as a network fault and is hard to attribute. Four things mitigate it — renewal grace is explicit and tested, the quota is derived rather than duplicated, the rates match the reference's proven values, and the drain-rate test asserts normal traffic never approaches the threshold.

A second, accepted risk: COMPEL's ten-second renewal covers a longer window than the reference's five-second refresh, so a burst inside one window is tolerated where the reference might have acted. The sustained rate is what matters, and a longer window is more forgiving rather than less.

A third: the seen-counter set is sized from the quota, so a larger renewal interval costs proportionally more memory per session. At the intended values this is roughly 9.3 kilobytes per session across the six retained windows at the game quota, still irrelevant, but the coupling is noted so a much longer interval is not chosen carelessly.
