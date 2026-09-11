# Proxy Abuse Protection — Deliberately Deferred Work

Companion to `2026-09-10-proxy-abuse-protection-design.md`. Everything here was found during that work, judged real, and **deliberately not fixed** in it. Each entry says what it is, what it costs, why it was parked, and what fixing it would take.

Nothing here blocks the abuse protection from being an improvement on the transparent relay it replaced. Item 1 is the only one that is worse than that baseline, and it is the one to read first.

---

## 1. A spoofed source can silence a named player

**The sharp one.** Scoring is keyed on `IPEndPoint` and UDP source addresses are forgeable, so anyone who knows a player's address and port can raise *that player's* violation score.

An unmatched challenge costs `ChallengeViolationWeight` (100) plus the arrival, and `ActionableThreshold` is 4000. So roughly **forty spoofed datagrams action a named player**, after which their own traffic is refused at the allowance check until the score drains at 140 a second. Repeating forty datagrams every few seconds sustains it indefinitely, at no measurable cost to the attacker.

Two things make the victim easy to target:

- The attacker needs the victim's endpoint. In a match that is obtainable.
- `challengeSequence` is a per-forwarder monotonic counter, so challenges issued to co-located sessions within one rotation are **consecutive integers**. A client holding value 17 can infer its co-players hold roughly `{11…20}`. That turns "guess a 32-bit value" into "try nine", which additionally allows *injecting* traffic into the match as the victim, and marks the victim's session authenticated as a side effect.

**Is it a regression?** Partly. Injecting spoofed traffic was already possible — before this work the proxy relayed any datagram unconditionally. *Silencing* a player is new: the branch is what added a score that spoofed traffic can drive.

**Why parked.** The underlying problem is inherent to per-source scoring over a forgeable transport, and the reference has it too — worse, in fact, since its response is a firewall ban on the whole IP, which also takes out anyone sharing it. Fixing it properly is a design question, not a patch.

**Note that randomising the challenge does not fix this.** It closes the injection half (the attacker can no longer produce a *matching* challenge) but not the silencing half, because an *unmatched* challenge is exactly what charges the victim.

**Candidate fixes, in rough order of promise:**

- **Exempt a behaving source from the actioned drop.** A source that has authenticated and whose recent datagrams are valid, in-quota and non-duplicate is by definition not abusing, whatever its score says. Spoofed traffic would then raise the victim's score without refusing the victim's own traffic. The catch is that the actioned check is also what stops a flood of *valid* traffic, so the per-packet arrival cost would have to keep carrying that case alone.
- **Cap what unknown-challenge violations alone can contribute**, so spoofing cannot reach the threshold without help from violations the victim's own traffic would have to commit.
- **Make the challenge value unpredictable**, which closes the injection half and is now cheap (see item 5).

---

## 2. Neither sessions nor connections are capped

`GetOrCreateSession` runs before the length guard and before the allowance check, so **one datagram from a novel source endpoint** buys a UDP socket, a pump task with a 65,535-byte buffer, a linked `CancellationTokenSource` and a `SessionChallengeState`, all held for the two-minute idle timeout — and an immediate 58-byte challenge to whatever address it named.

The reference bounds this three ways COMPEL has none of: `MAX_IDLE_TIME` 15 seconds (`main.cpp:42`), `MAX_GAME_CONNECTIONS` 24 and `MAX_GAME_CONNECTIONS_PER_IP` 10 (`main.cpp:53-56`), and refusing all new connections while its under-attack indicator is over threshold.

**Why parked.** Entirely pre-existing — the session machinery predates this work, and the abuse protection deliberately sits *after* it. Capping needs a policy decision about what the limits should be given COMPEL's configurable instance count, which is a question for the operator rather than a defect to fix.

**What fixing it takes.** A per-forwarder session cap and a per-IP cap, checked before `GetOrCreateSession`, plus a decision on whether to refuse new sessions while `IsUnderAttack` holds. The idle timeout could also be shortened for a session that has never authenticated, which is already tracked.

---

## 3. Retained challenges are per session, not per forwarder

The reference keys `responses` on the challenge value **globally**, with a per-address inner map, so a client whose source port changes hits the inner miss at `main.cpp:786-789`, which creates a fresh counter array and admits the datagram. COMPEL keys them per `ClientSession`, so a NAT rebind produces a session holding no history while the client — keying its own challenge on our unchanged public port — legitimately keeps echoing one we no longer recognise.

Task 9 worked around this with a grace: until a session has authenticated, and for at most thirty seconds, an unmatched challenge is refused without the violation weight. That stops the false positive but still drops about a second of the client's traffic on a rebind.

**Why parked.** It re-keys three types (`ChallengeWindow`, `SessionChallengeState`, and the forwarder's use of both), and Task 9 was the merge blocker.

**What fixing it takes.** Move the retained challenge values to `UDPForwarder`, keep a per-`(challenge, endpoint)` seen set, and evict a challenge's whole set when the challenge ages out — which is what the reference does. It makes the challenge value shared across sessions, which is what would make randomising it worthwhile. The per-session grace **stays** either way: it is working, it is tested, and replacing it with a re-keying refactor would be churn for its own sake. Treat global retention as an improvement layered on top, not as a reason to remove the grace.

---

## 4. The unknown-challenge grace has an untested bound

`ClientSession.IsWithinUnknownChallengeGrace` reads `Environment.TickCount64` inline, so the thirty-second bound cannot be reached by a test without actually waiting. Setting `UnknownChallengeGraceMilliseconds` to `long.MaxValue` — removing the only limit on how long a hostile source keeps the 100-weight suppressed — leaves the whole suite green.

**Why parked.** The bound's security value is low: the grace only suppresses a charge on a path that drops the datagram either way, it buys an attacker at most thirty seconds of un-actioned dropping, and rotating source ports is cheaper for an attacker than waiting out a grace. Against that, testing it means injecting a `TimeProvider` into `UDPForwarder` and `ClientSession`, rippling through `UDPProxyService` and every test rig.

**What fixing it takes.** That injection. `ViolationScoreContainer` already takes a `TimeProvider` and `ControllableTimeProvider` already exists in the test project, so the pattern is established — it is purely the constructor churn. Doing so would also make the idle-session timeout testable, which it currently is not.

---

## 5. The challenge value is guessable

`challengeSequence` is a monotonic counter where the reference draws from a CSPRNG (`main.cpp:1223`, retried at `:1227` against its whole retained list). A source that guesses a live challenge is held to the per-challenge quota (1440) rather than the much smaller unauthenticated total (100), and watermark validation — if it is ever added — would depend on the value being unpredictable.

**Why parked, and why that reason has now expired.** It was parked because the challenge value and the server-creation timestamp shared one field, so randomising the value meant separating them on a path that worked in production. **Task 9 separated them.** The stated blocker is gone and this is now a small, self-contained change.

**What fixing it takes.** Draw the value from `RandomNumberGenerator`, and honour the precondition already documented on `SessionChallengeState.Rotate`: never reissue a value the session still retains. A monotonic counter satisfied that for free; a random one does not. Worth doing together with item 3, which makes the value shared and therefore worth protecting.

---

## 6. Watermark, packet-type and netcmd validation

Deferred from the original design, recorded as TODOs in `UDPProxyService`. The reference's constant per-region watermark and dynamic CRC32C watermark are its highest-value anti-cheat signal; adding them needs a region setting COMPEL has no equivalent for, and they carry a higher false-positive cost than any check currently implemented. Packet-type and network-command validity need `game_data_protocol.h` ported.

---

## 7. The game-command quota is advertised but not enforced

`ChallengeQuota.GameCommandForInterval` derives a quota, and the challenge packet advertises it, so a compliant client self-limits to it. Nothing on the proxy side checks it, because the reference enforces it with a second per-challenge counter that only increments for `NETCMD_CLIENT_GAME_DATA` packets carrying order commands (`main.cpp:764`) — which needs the netcmd parsing deferred in item 6.

Worth knowing that the client's *soft* limiter only watches the total counter, never the game-command counter, so a client that exceeds the game-command quota hits its hard limit with no back-pressure warning. That is the client's behaviour, not something COMPEL can change.
