# Proxy Abuse Protection — Deliberately Deferred Work

Companion to `2026-09-10-proxy-abuse-protection-design.md`. Everything here was found during that work, judged real, and **deliberately not fixed** in it. Each entry says what it is, what it costs, why it was parked, and what fixing it would take.

Two entries here are worse than the transparent relay this replaced, and they are the ones to read first. Item 3 blackholes a legitimate player for the rest of a match whenever two sessions share one public port, which is why it blocks the merge; item 1 lets a spoofed source silence a named player, which the relay had no score to make possible. The opening claim that nothing here blocked the work from being an improvement on that baseline did not survive live verification.

## Triage

Corrected after live verification. Every item is in one of four containers, and the actionable list is `docs/superpowers/plans/2026-09-11-proxy-hostile-traffic-hardening.md`.

**MUST HAVE, BLOCKING — item 3.** Live testing against a real client showed this is not the theoretical divergence it was first triaged as: because the client keys the challenge it holds by *destination* while COMPEL keys retained challenges by *session*, two sessions sharing one public port collide on every rotation and the loser is charged 100 a datagram and blackholed. Measured at 3306 dropped datagrams in 441 seconds with `proxyIsUnderAttack` true. **This item was originally triaged as SKIP on the reasoning that the per-session grace covered it. That reasoning was wrong — the grace applies only before a session authenticates and this collision happens after.** Item 5 is promoted with it, because a shared challenge is what makes an unpredictable value worth having.

**MUST HAVE, BEFORE HOSTILE EXPOSURE — items 1, 2, 8.** The proxy is internet-reachable. Item 2 (no session or connection cap) lets one datagram buy a socket and a task; item 1 (a spoofed source silencing a named player) is the one behaviour worse than the transparent relay this replaced; item 8 (the under-attack indicator's five-minute lag) has to be fixed before anything gates admission on it.

**NICE TO HAVE — items 4, 10, plus one new. Item 4 is done as of 2026-09-11.** None of the three changes behaviour; each changes how much the next change can be trusted, and item 10 remains a `TODO` at the code site it concerns. Item 10 — extracting the pipeline out of `UDPForwarder.Run` — is the highest-leverage item here: the pipeline being inline is *why* the blocking defect needed a live client to surface. The new item is a test double modelling the client's challenge storage, which is the specific gap the blocker fell through.

**SKIP — items 7, 9, and the challenge expiry.** Declined with reasons in the plan. Item 9 only becomes reachable if the caps are not done; item 7 cannot be done without the netcmd parsing in item 6.

**A PROJECT OF ITS OWN — item 6.** Watermark, packet-type and network-command validation stays a `TODO` in the code and deserves its own spec.

## Effort, for the record


Effort is given in **tasks**, where a task is one focused change with its own tests and its own review — roughly what fits comfortably in one sitting.

**These tables were corrected on 2026-09-11 to match the triage above.** They previously filed item 3 as "genuinely optional" and item 5 as "cheap, whenever", and opened by saying nothing here was required before the feature ships. Live testing promoted both to merge blockers, so all three statements were wrong.

**Blocking the merge.**

| | Item | Effort | Notes |
| --- | --- | --- | --- |
| 3 | Per-forwarder challenge retention | **1–2 tasks** | The merge blocker. Re-keys the retained challenges and the sequence onto `UDPForwarder` with a per-`(challenge, endpoint)` seen set. The per-session grace stays. |
| 5 | Make the challenge value random | **Half a task** | Immediately after item 3, which is what makes it meaningful. The blocker was removed during this work. Needs the uniqueness check the `Rotate` precondition already documents. |

**Before this is deliberately exposed to hostile traffic** — the proxy is reachable from the internet, so this is the group that matters if anyone attacks it rather than merely plays against it.

| | Item | Effort | Notes |
| --- | --- | --- | --- |
| 2 | Session and connection caps | **1–2 tasks** | Mostly a policy decision: what the caps should be given a configurable instance count. The code is a check before `GetOrCreateSession` plus per-IP counting. The lifetime half is already closed by the fifteen-second unauthenticated timeout; only the count caps remain. |
| 1 | Spoofed source silences a player | **1 session** | Half of it is deciding *which* mitigation; the implementation after that is small. Do not start it as an implementation task. |
| 8 | Under-attack indicator is stale | **1 task** | Only worth doing as part of item 2, since gating admission on it is the only use that needs it live. |

**To make future work in this area safe** — none of it changes behaviour, all of it changes how much you can trust the next change.

| | Item | Effort | Notes |
| --- | --- | --- | --- |
| 10 | Extract the pipeline out of `UDPForwarder.Run` | **1–2 tasks** | Highest leverage item in this document. Several defects on this branch were only reachable through a live loopback socket because the pipeline is inline; extracting it makes them unit-testable. |
| 4 | Inject a `TimeProvider` into the forwarder | **Done 2026-09-11** | Took the one mechanical task estimated. Six tests added, each proven against a mutation; the grace bound and both idle timeouts are now covered. |
| — | Model the client's challenge storage in a test double | **1 task** | The specific gap the merge blocker fell through. Nothing in the suite models the client keying challenges by destination and accepting a replacement only on a strictly greater timestamp. |

**Deliberately skipped**, with the reasons recorded in Part 4 of the plan so they are not re-argued: item 7 (enforce the game-command quota, which needs item 6's netcmd parsing), item 9 (parallelise the maintenance loop — **1 task**, but item 2's caps remove the condition that makes it reachable), and reducing the challenge expiry.

**Large, and genuinely optional.**

| | Item | Effort | Notes |
| --- | --- | --- | --- |
| 6, 7 | Watermark, packet-type and netcmd validation | **Several sessions** | The reference's highest-value anti-cheat signal and the highest false-positive risk in the whole area. Needs a region setting COMPEL has no equivalent for, and ports from two trees: `game_data_protocol.h` beside the reference, and the HON client's `k2_protocol.h` for the packet flags and network commands the reference never defines. Treat as its own project with its own spec. |

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

`GetOrCreateSession` runs before the length guard and before the allowance check, so **one datagram from a novel source endpoint** buys a UDP socket, a pump task with a 65,535-byte buffer, a linked `CancellationTokenSource` and a `SessionChallengeState` — and an immediate 58-byte challenge to whatever address it named.

The reference bounds this three ways, of which COMPEL now has one. `MAX_IDLE_TIME` 15 seconds (`main.cpp:42`) is ported as `UnauthenticatedSessionTimeout`, so a session that never authenticates is held for fifteen seconds rather than the two-minute idle timeout — effectively fifteen to twenty-five, since eviction only runs on a rotating pass. What COMPEL still has none of is `MAX_GAME_CONNECTIONS` 24 and `MAX_GAME_CONNECTIONS_PER_IP` 10 (`main.cpp:53-56`), and refusing all new connections while its under-attack indicator is over threshold. A flood fast enough simply outruns the fifteen-second sweep.

**Why parked.** Entirely pre-existing — the session machinery predates this work, and the abuse protection deliberately sits *after* it. Capping needs a policy decision about what the limits should be given COMPEL's configurable instance count, which is a question for the operator rather than a defect to fix.

**What fixing it takes.** A per-forwarder session cap and a per-IP cap, checked before `GetOrCreateSession`, plus a decision on whether to refuse new sessions while `IsUnderAttack` holds. Shortening the idle timeout for a session that has never authenticated was the remaining part of this and is **done**: that is what `UnauthenticatedSessionTimeout` is.

---

## 3. Retained challenges are per session, not per forwarder

The reference keys `responses` on the challenge value **globally**, with a per-address inner map, so a client whose source port changes hits the inner miss at `main.cpp:786-789`, which creates a fresh counter array and admits the datagram. COMPEL keys them per `ClientSession`, so a NAT rebind produces a session holding no history while the client — keying its own challenge on our unchanged public port — legitimately keeps echoing one we no longer recognise.

Task 9 worked around this with a grace: until a session has authenticated, and for at most thirty seconds, an unmatched challenge is refused without the violation weight. That stops the false positive but still drops about a second of the client's traffic on a rebind.

**No longer parked — this is now the merge blocker.** Live testing showed the collision is routine rather than theoretical: the client used eight source ports in one short run, several live on the same public port, and every rotation issued a different challenge to each session while the client stored only the last one for that destination. The session holding the others was charged 100 a datagram and blackholed. See Task 1 of the plan for the evidence and the fix.

**What fixing it takes.** Move the retained challenge values to `UDPForwarder`, keep a per-`(challenge, endpoint)` seen set, and evict a challenge's whole set when the challenge ages out — which is what the reference does. It makes the challenge value shared across sessions, which is what would make randomising it worthwhile. The per-session grace **stays** either way: it is working, it is tested, and replacing it with a re-keying refactor would be churn for its own sake. Treat global retention as an improvement layered on top, not as a reason to remove the grace.

---

## 4. The unknown-challenge grace has an untested bound — RESOLVED 2026-09-11

**Fixed.** `UDPForwarder` and `ClientSession` now take a `TimeProvider` and read every clock through it, so the grace's bound and both idle timeouts are reachable by a test. Six tests cover them, each proven to fail against a specific mutation. `UnknownChallengeGrace` moved to `UDPForwarder` as an `internal static readonly TimeSpan`, and the two idle timeouts became `internal`, so a test can cite the real values rather than a copy of them.

The record of why it mattered, kept because it is the evidence: `ClientSession.IsWithinUnknownChallengeGrace` read `Environment.TickCount64` inline, so the thirty-second bound could not be reached by a test without actually waiting. Setting `UnknownChallengeGraceMilliseconds` to `long.MaxValue` — removing the only limit on how long a hostile source keeps the 100-weight suppressed — left the whole suite green: measured on 2026-09-11 at 139 passed, 0 failed.

**One trap this work turned up, worth carrying forward.** The first version of the grace test advanced the clock by `UnknownChallengeGrace` itself, and so still passed with the grace widened to `TimeSpan.MaxValue` — the advance widened with the constant. A duration that is the thing under test has to be pinned as a literal in its own test; behavioural tests may then read the constant freely. That is now a global constraint in the plan.

**Why it was parked.** The bound's security value was judged low: the grace only suppresses a charge on a path that drops the datagram either way, it buys an attacker at most thirty seconds of un-actioned dropping, and rotating source ports is cheaper for an attacker than waiting out a grace. Against that, testing it meant injecting a `TimeProvider` into `UDPForwarder` and `ClientSession`, rippling through `UDPProxyService` and every test rig. That ripple turned out to be smaller than feared — four call sites and one override.

**What fixing it took.** The injection, as expected: purely constructor churn across `UDPForwarder`, `ClientSession`, `UDPProxyService` and three test construction sites, following the pattern `ViolationScoreContainer` already set. `ControllableTimeProvider` also needed a `GetUtcNow` override, because it had only ever overridden `GetTimestamp` and the challenge timestamp reads the wall clock — without it a moved clock would have left the two out of step. As predicted, it made both idle-session timeouts testable at the same time.

---

## 5. The challenge value is guessable

`challengeSequence` is a monotonic counter where the reference draws from a CSPRNG (`main.cpp:1223`, retried at `:1227` against its whole retained list). A source that guesses a live challenge is held to the per-challenge quota (1440) rather than the much smaller unauthenticated total (100), and watermark validation — if it is ever added — would depend on the value being unpredictable.

**Why parked, and why that reason has now expired.** It was parked because the challenge value and the server-creation timestamp shared one field, so randomising the value meant separating them on a path that worked in production. **Task 9 separated them.** The stated blocker is gone and this is now a small, self-contained change.

**What fixing it takes.** Draw the value from `RandomNumberGenerator`, and honour the precondition already documented on `SessionChallengeState.Rotate`: never reissue a value the session still retains. A monotonic counter satisfied that for free; a random one does not. Worth doing together with item 3, which makes the value shared and therefore worth protecting.

---

## 6. Watermark, packet-type and netcmd validation

Deferred from the original design, recorded as TODOs in `UDPProxyService`. The reference's constant per-region watermark and dynamic CRC32C watermark are its highest-value anti-cheat signal; adding them needs a region setting COMPEL has no equivalent for, and they carry a higher false-positive cost than any check currently implemented. Packet-type and network-command validity need definitions from two trees: `game_data_protocol.h`, which sits beside the reference and holds the `GAME_CMD_*` table, and the HON client's `src/k2/k2_protocol.h`, which holds the `PACKET_*` flags and `NETCMD_*` values that the reference uses but never defines.

---

## 7. The game-command quota is advertised but not enforced

`ChallengeQuota.GameCommandForInterval` derives a quota, and the challenge packet advertises it, so a compliant client self-limits to it. Nothing on the proxy side checks it, because the reference enforces it with a second per-challenge counter that only increments for `NETCMD_CLIENT_GAME_DATA` packets carrying order commands, tested against `CHALLENGE_MAX_GAME_CMD_CTR` at `main.cpp:764` — which needs the netcmd parsing deferred in item 6.

Worth knowing that the client's *soft* limiter only watches the total counter, never the game-command counter, so a client that exceeds the game-command quota hits its hard limit with no back-pressure warning. That is the client's behaviour, not something COMPEL can change.

---

## 8. The under-attack indicator lags by up to five minutes

`IsUnderAttack` is recomputed only when a five-minute window closes, so a flood is invisible for up to five minutes and the flag stays set for up to five minutes after one ends.

**Corrected on 2026-09-11: only the first half of that is a divergence from the reference.** The reference's indicator is reset to zero on its own five-minute housekeeping tick (`main.cpp:1248-1253`), so its flag also stays set for up to five minutes after an attack ends; it is an accumulating counter, not a decaying one. What it does differently is *where it is read*: it both increments and tests per datagram on the new-connection path (`:1714` for game, `:1383` for voice), refusing the datagram outright, so it takes effect the instant the count crosses the threshold. COMPEL's cannot rise until the window closes. It is also weighted, where COMPEL's is a raw count of refused datagrams — and the reference's commonest weight, +1 per datagram from an unknown source, is not a refusal at all, so the two quantities do not measure the same thing.

**Why this matters for item 2.** Item 2 suggests refusing new sessions while `IsUnderAttack` holds. Built on this indicator that would gate admission on data up to five minutes stale. The indicator has to become live before it can be used as a gate — recorded here so the next person does not discover that after wiring it up. Making it *decay* rather than window-reset fixes the stuck-flag half as well, and is an improvement on the reference rather than parity with it.

---

## 9. The maintenance loop is a single serial task across all forwarders

`RotateChallenges` and `RepeatChallenges` are synchronous and issue a blocking `SendTo` per session, inside one loop that also runs the score drain and covers every forwarder. At legitimate load this is trivial. Combined with item 2's uncapped session table, one attacked public port can stall the shared loop, delaying challenge rotation for every other instance's players and delaying the drain that is the only way an actioned source recovers. The transparent relay had no shared serial path, so this cross-instance coupling is new.

---

## 10. `UDPForwarder` carries the whole pipeline inline

`Run` holds the validation pipeline across roughly a hundred lines of the receive loop, and the class is now 495 lines mixing socket lifetime, the session table, challenge issuing, validation and drop accounting. The pure pieces were extracted into their own types; the pipeline was not, which is why several defects in it were only reachable through a live loopback socket rather than a unit test. `ClientSession` is a private nested class, so the two flags the repeat and the grace turn on have no direct test surface at all.
