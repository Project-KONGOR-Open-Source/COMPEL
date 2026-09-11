# Proxy Follow-Up Work Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement the tasks in Part 1 and Part 2 task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Everything outstanding on COMPEL's managed UDP proxy after the abuse-protection work, triaged into what must be done, what is worth doing, and what is deliberately not being done.

**Spec:** `docs/superpowers/specs/2026-09-10-proxy-abuse-protection-design.md` is the design the shipped work argues from. `docs/superpowers/specs/2026-09-11-proxy-abuse-protection-follow-up.md` holds the long-form reasoning for each item below and the evidence behind it; this document is the actionable list.

**Tech Stack:** .NET 11, ASP.NET Core with Native AOT, TUnit on the Microsoft Testing Platform.

## How to use this document

It is written to be picked up cold across several sessions. Work top to bottom: Part 1 blocks the merge, Part 2 blocks exposure to hostile traffic, Part 3 is worth doing whenever, Part 4 is closed.

**Each task states intent, the verified facts behind it, and acceptance criteria — deliberately not finished code.** That is a lesson from the work that produced this list: eleven defects were traced to plan text that pre-wrote code, which then went stale or contradicted what the surrounding code already asserted. Write the code in the session that runs the task, against the reference read fresh.

## Global Constraints

- Reference implementation: `source/COMPEL/bin/Publish/HoN_Proxy/HoN/branches/retail/Tool/HoNProxy/main.cpp`. `game_data_protocol.h` sits beside it in that directory. The HON client is at `C:/Users/SADS-810/Source/HON/src/k2/c_enhanced_watermark.cpp` and `c_hostclient.cpp`, and the packet flags and network commands the reference uses but does not itself define are in `C:/Users/SADS-810/Source/HON/src/k2/k2_protocol.h`.
- **Re-read every cited reference line before using it.** Six reviews of the previous work each corrected a documented claim about the reference or the client. Assume this document is wrong until checked. Every citation here was last checked on 2026-09-11, a pass that corrected the under-attack citation and every weight in Task 4, the per-address cap semantics in Task 3, and the header locations in Part 5.
- **The client is part of the system.** The defect that blocked the merge was invisible until someone read how the client stores challenges. When a task concerns the challenge protocol, read the client, not only the proxy.
- The datagram pipeline's ordering is a security property: charge the arrival, refuse an actioned source, length-guard, read the fields, match the challenge, refuse an unmatched one, admit the counter once, relay. The session and its first challenge are created *before* all of that, which is the whole of what Task 3 changes.
- The datagram path must not await and must not allocate per datagram.
- Never `var`; explicit types. Acronyms upper-case in PascalCase (`IPEndPoint`, `UDPForwarder`), camelCase only when not leading. Full words, never abbreviations. British English. Four spaces. Comments in StartCase; XML summaries in sentence case with a full stop per sentence and no sentence split across lines. Symbol references in double quotation marks or a parameterless `<see cref="..."/>`. Never the null-forgiving operator. A constant ported from the reference carries its `#define` name in a trailing comment.
- `dotnet build source/COMPEL.slnx` reports 0 warnings after every task.
- **Prove every new test fails against the pre-fix code**, by stashing the production change and running it, and record the figures. Three tests in the previous work appeared to pin an invariant and did not.
- **Beware the self-cancelling mutation.** A test that derives its input from the constant it is testing cannot fail when that constant changes. The `TimeProvider` work hit this: a test advancing the clock by `UnknownChallengeGrace` still passed with the grace widened to `TimeSpan.MaxValue`, because the advance widened with it. Where a duration is the thing under test, pin its value as a literal in its own test and let the behavioural tests read the constant.
- **Native AOT publish:** run `scripts/Publish-Native-AOT-Release.ps1` with `pwsh`, redirect to a file, and read the script's own exit code before filtering. `powershell` on this machine is 5.1 and the script requires 7; a piped `grep` will hide that it refused to run.

---

# Part 1 — Must have, blocks the merge

## Task 1: Hold Retained Challenges Per Forwarder, Not Per Session

**This is the merge blocker. It was found by live testing against a real client, after five code reviews and 139 passing tests missed it.**

**The defect.** COMPEL keys retained challenges per `ClientSession`. The client keys the challenge it holds per **destination** — `challenges[ip_cstr]` in `c_enhanced_watermark.cpp`, where the key is the server's address and port. So whenever two sessions share one public port, each rotation issues a *different* challenge to each session, the client stores only whichever arrived last for that destination, and every other session on that port is then echoing a value it does not retain.

**The evidence**, from an instrumented run against a live match on public port 21236:

```
DIAG ISSUE  127.0.0.1:60099  Challenge 56  Timestamp 1789118118
DIAG ISSUE  127.0.0.1:60097  Challenge 57  Timestamp 1789118118

DIAG UNMATCHED 127.0.0.1:60097  Echoed 56  Counter 85..392  Retained [57,54,50,47]  Grace False
```

`Grace False` — the session had already authenticated, so the unknown-challenge grace does not apply and each datagram is charged `ChallengeViolationWeight`. The source is actioned within about forty datagrams and blackholed for the rest of the match. The run reached 3306 dropped datagrams in 441 seconds with `proxyIsUnderAttack` true.

This is routine, not exotic: the client used eight source ports in one short run, several live on the same public port at once.

**Why the existing grace does not cover it.** The grace applies only before a session has authenticated. This collision happens after.

**What the reference does.** `responses` is keyed on the challenge **value** globally, with a per-address inner map (`main.cpp:189`, looked up at `:692`). A datagram whose challenge is known but whose address is new hits the inner miss at `main.cpp:786-789`, which *creates* a counter array and admits the datagram. That is why the reference has no equivalent bug.

- [x] **Step 1: Fact verification.** Read `main.cpp:189`, `:692`, `:786-789` and `:1217-1245` for the retention and eviction shape. Read `c_enhanced_watermark.cpp` around the `challenges[ip_cstr]` assignment to confirm the client's key is the destination. Confirm `KEEP_CHALLENGES` is 6 and that a challenge's whole inner map is erased when the value is evicted.
- [x] **Step 2: Write the failing test — this is the test that was missing.** Two sessions on one forwarder, a rotation, then the *first* session echoing the challenge issued to the *second*. It must be admitted. Assert it against the forwarder, not a unit, because that is where the two sessions exist. Prove it fails today.
- [x] **Step 3: Move the retained challenges and the sequence to `UDPForwarder`.** One sequence and one bounded history of `RetainedChallengeCount` values per forwarder. Keep a per-`(challenge, endpoint)` seen set so two clients under one challenge have independent counters. Evicting a challenge drops its whole set, as the reference does.
- [x] **Step 4: Keep the per-session grace.** It still covers a client echoing a challenge from a *previous COMPEL run*, which live testing showed costs exactly one datagram and resolves in milliseconds. Do not remove it.
- [x] **Step 5: Re-verify live.** A real match, then a second client or a forced source-port change on the same public port, with `proxyDroppedDatagramCount` staying at zero.
- [x] **Step 6: Commit.** (`c3f0244`)

## Task 2: Make The Challenge Value Unpredictable

**Do this immediately after Task 1, which is what makes it meaningful.** Once the challenge is shared per forwarder rather than per session, an unpredictable value actually protects something.

**Why.** A per-forwarder monotonic counter issues *consecutive* values, so a client holding 17 can infer its co-players hold roughly `{11…20}` — enough to inject traffic into a match as another player. The reference draws from a CSPRNG and retries against its whole retained list (`main.cpp:1223`, `:1227`).

**Why it is now cheap.** The value and the server-creation timestamp used to share one field. That coupling was removed, so this is self-contained.

- [x] **Step 1: Fact verification.** Confirm the CSPRNG draw and the retry-against-retained-list at the cited lines.
- [x] **Step 2: Write the failing tests.** Two successive challenges are not consecutive; a value is never zero, because zero marks an unauthenticated client; a value still retained is never reissued.
- [x] **Step 3: Implement** with `RandomNumberGenerator`, honouring the no-reissue precondition already documented on the rotation path. A monotonic counter satisfied that for free; a random one does not.
- [x] **Step 4: Commit.** (`79b31c1`)

---

# Part 2 — Must have before the proxy is deliberately exposed to hostile traffic

The proxy is reachable from the internet. These do not block a merge but they do block treating it as hardened.

## Task 3: Cap Sessions Per Forwarder And Per Source Address

**The hole.** `GetOrCreateSession` runs before the length guard and before the allowance check, so one datagram from any novel source endpoint buys a UDP socket, a pump task with a 65,535-byte buffer, a `CancellationTokenSource`, a `SessionChallengeState`, and a challenge datagram to whatever address it named — with no cap. A spoofed-source flood exhausts sockets and file descriptors, and the abuse score cannot help because every spoofed endpoint is new.

**What is already bounded, so that this task is not over-scoped.** The *lifetime* half of this hole is closed: `UnauthenticatedSessionTimeout` (15 seconds, `MAX_IDLE_TIME`) already sweeps a session that never authenticates, at an effective fifteen to twenty-five seconds because eviction only runs on a rotating pass. What remains is purely the absence of a *count* cap, which is what lets a fast enough flood outrun that sweep.

The reference caps at `MAX_GAME_CONNECTIONS` 24 and `MAX_GAME_CONNECTIONS_PER_IP` 10, with separate `MAX_VOICE_CONNECTIONS` 24 and `MAX_VOICE_CONNECTIONS_PER_IP` 10 (`main.cpp:53-56`), enforced before a connection is admitted (`:1718`, `:1739`).

**The two enforcement sites do not agree, and the difference is a whole connection.** The total cap is `openConnections >= MAX_GAME_CONNECTIONS` (`:1718`), so 24 is a true ceiling. The per-address cap is `openGameConnectionForIP > MAX_GAME_CONNECTIONS_PER_IP` (`:1739`) against a count of *existing* connections for that address, so it admits an eleventh. Decide which COMPEL implements rather than inheriting the off-by-one by transcription. Note also that both per-address refusals (`:1739` for game, `:1742` for voice) `continue` without releasing the lock they hold, where every refusal above them unlocks first — so these lines are a source of constants and intent, not a shape to mirror.

**Decide the numbers first.** COMPEL runs one forwarder per instance per kind, and `Instances` is configurable up to the logical processor count, so a per-forwarder cap of 24 on a sixteen-core host permits up to 384 game sessions and 384 voice sessions before any cap binds. Twenty-four per forwarder matches the reference and a full match plus spectators; confirm against `MatchServerManagerOptions` whether a busy host wants more, and whether the per-address cap should be ten.

- [x] **Step 1: Fact verification** of all four constants and both enforcement sites, including which comparison each uses.
- [x] **Step 2: Write the failing tests.** A forwarder at its cap refuses a novel endpoint without creating a session; an address at its per-address cap is refused while a different address is admitted; a session freed by eviction releases its slot.
- [x] **Step 3: Implement.** Check both caps before constructing anything. Count live sessions per `IPAddress`, decremented wherever a session dies. Note that this is **not** the same set as the sites `ReleaseDropReport` is called from: of those three, only the pump's `finally` and `EvictIdleSessions` are session deaths, the third being the orphaned-report reclaim loop that runs after eviction; and `Dispose` tears down every remaining session without calling it at all. A refused datagram takes the unweighted drop: being the twenty-fifth player is not the client's fault.
- [x] **Step 4: Commit.** (`954573a`)

## Task 4: Make The Under-Attack Indicator Live

**Why it must precede any use of it.** `IsUnderAttack` is recomputed only when a five-minute window closes, so a flood cannot raise the flag for up to five minutes. Gating admission on it as it stands would gate on data up to five minutes stale.

**What the reference actually does, corrected.** Its indicator is an accumulating `atomic<uint64_t>` (`:203`) that is **reset to zero every five minutes** in the one-second housekeeping tick (`:1248-1253`) — the same window shape as COMPEL's, not a decaying one. The real divergence is *where it is read*: on the new-connection path the reference both increments and tests it per datagram, `++under_attack_indicator > UNDER_ATTACK_THRESHOLD` refusing the datagram outright (`:1714` for game, `:1383` for voice), so its flag takes effect the instant the count crosses 1000. COMPEL's cannot change until the window closes. **So "the flag stays set for up to five minutes after an attack ends" is true of the reference too** — that half is not a defect against parity, and only the rise latency is.

**A decaying counter is therefore an improvement on the reference, not parity with it.** That is the right design, because it fixes the stuck-flag half as well, but it must be recorded as a deliberate divergence so that a later reader does not "correct" it back or try to verify it against a reference that does not behave that way.

**The weights, as read rather than as previously written down.** +1 per datagram from a source with no existing socket (`:1714`, `:1383`); +10 when refused by the total connection cap (`:1719`, `:1388`); +100 per blocked datagram from a new connection (`:584`, `:600`, `:616`, `:648`, `:729`, `:841`) and on firewall-banning a player (`:552`); +10000 when the firewall ban table saturates at a thousand entries (`:565`); +2 at `:1783`. The threshold is `UNDER_ATTACK_THRESHOLD` 1000 (`:52`), which COMPEL already ports.

**Note what this changes about the quantity.** COMPEL's indicator counts *refused datagrams*. The reference's counts weighted events of which the commonest, +1 per datagram from an unknown source, is not a refusal at all. The weights cannot be ported without deciding what COMPEL's indicator is now measuring.

Beware `:1041`, which an earlier draft of this document cited for the per-datagram read and which says nothing of the kind: it sits in the server-to-client relay's error handler, where an indicator *above* threshold suppresses the idle teardown of a failing socket.

- [x] **Step 1: Fact verification.** Read every `under_attack_indicator` site and confirm the weights and reset above, and that the per-datagram read is at `:1714` and `:1383`.
- [x] **Step 2: Write the failing tests.** The indicator rises within one maintenance pass of a burst rather than one window, and falls once the burst stops. If the reference's weights are adopted, pin their *relative* order as the reference has it — a datagram blocked by validation weighs 100 against a cap refusal's 10, which is the opposite way round from how this document first stated it.
- [x] **Step 3: Implement** as a decaying weighted counter updated on the drop path, using the same elapsed-proportional drain `ViolationScoreContainer` already uses and tests, and record the divergence from the reference's window reset where the constant is declared.
- [x] **Step 4: Gate admission** on it, as the reference does at `:1714`. An existing session is unaffected, so a match in progress is never interrupted.
- [x] **Step 5: Commit.** (`766275b`)

## Task 5: Stop A Spoofed Source Silencing A Player

**This task needs its design confirmed with the repository owner before any code. Do not start it as a transcription task.**

**The hole.** Scoring is keyed on `IPEndPoint` and UDP source addresses are forgeable. An unmatched challenge costs 100 against a threshold of 4000, so roughly **forty spoofed datagrams action a named player**, after which their own traffic is refused until the score drains. Repeating forty datagrams every few seconds sustains it at no measurable cost. This is the one behaviour in the abuse protection that is worse than the transparent relay it replaced, which had no score to drive.

**The reference is no help.** Its `warns` map is keyed on address and port too, and its response is worse — a firewall ban on the whole IP, which also removes anyone sharing it.

**Proposed design, to be confirmed.** Separate what the score *records* from what it is allowed to *decide*: action a source on its sustained **arrival rate** alone — the per-packet cost against the drain, already implemented and tested — and let violation weights accumulate for logging, the under-attack indicator and diagnosis without gating the drop. Silencing a player then requires actually sustaining more than `EstimatedPacketsPerSecond` spoofed as them, which costs real bandwidth and is no cheaper than attacking them directly.

**What that gives up:** an invalid-traffic source is no longer short-circuited at the allowance check, so each of its datagrams is validated and dropped individually. Small cost on the flood path.

**Alternatives considered, recorded so they are not rediscovered.** Capping what unmatched-challenge violations alone may contribute is simpler but arbitrary and leaves a smaller version of the same asymmetry. Exempting a source whose current datagram validates protects the victim perfectly but makes the actioned state gate nothing, since valid traffic would always relay. Doing nothing is defensible only if the proxy is never exposed to a hostile player.

- [x] **Step 1: Confirm the design** with the repository owner, presenting the proposal and both alternatives. If rejected, re-plan rather than implementing a compromise. (Confirmed design)
- [x] **Step 2: Implement & verify.** Separate arrival score from violation score so actioning a source is driven by sustained arrival rate alone. Verified with test `Forty_Spoofed_Violation_Datagrams_Do_Not_Refuse_A_Victims_Valid_Traffic`. (`17f1d82`)

---

# Part 3 — Nice to have

Neither changes behaviour. Both change how much the next change can be trusted. Recorded as `TODO` comments at the code sites they concern.

- [x] **Extract the validation pipeline out of `UDPForwarder.Run`.** *Done 2026-09-11.* Extracted into `DatagramValidator` and `DatagramValidationResult`. Decoupled datagram length, challenge matching, and counter rate limits from socket IO into pure, zero-allocation methods tested by 8 dedicated unit tests in `DatagramValidatorTests.cs`. (`fe32289`)
- [x] **Inject a `TimeProvider` into `UDPForwarder` and `ClientSession`.** *Done 2026-09-11.* Both now read every clock through an injected provider: `EvictIdleSessions` measures with `GetElapsedTime`, `ClientSession` holds a provider timestamp (`LastActivityTicks` became `LastActivityTimestamp`, since it is no longer milliseconds), and `NextIssuedTimestamp` reads `GetUtcNow`. `UnknownChallengeGrace` moved to `UDPForwarder` as an `internal static readonly TimeSpan`, and `IdleSessionTimeout` and `UnauthenticatedSessionTimeout` became `internal` so a test can cite them. `ControllableTimeProvider` gained a `GetUtcNow` override anchored to a fixed epoch, so a moved clock cannot leave wall-clock readers out of step with elapsed-time readers. Six tests were added and each proven to fail against a specific mutation — the grace widened to `TimeSpan.MaxValue`, the grace bound deleted, the timeout choice forced either way, and eviction forced never/always. No behaviour changed: 145 tests pass, 0 build warnings, and the Native AOT publish is clean.
- [x] **Model the client's challenge storage in a test double.** *Done 2026-09-11.* Created `ClientChallengeStoreDouble` and `ClientChallengeStoreDoubleTests` capturing the native client's per-destination endpoint challenge storage and timestamp monotonicity rules. (`fe32289`)

---

# Part 4 — Deliberately skipped

Recorded with reasons so they are not rediscovered and re-argued.

- **Parallelise the maintenance loop.** It is a single serial task issuing blocking sends for every session of every forwarder, so under an uncapped session table one attacked port can stall challenge rotation and the score drain for every other instance's players. Skipped because Task 3's caps remove the condition that makes it reachable. Revisit only if Task 3 is not done.
- **Enforce the game-command quota.** It is advertised and the client self-limits to it, but nothing on the proxy side checks it, because the reference enforces it with a second per-challenge counter that only increments for order-carrying game-data packets (`main.cpp:764`). That needs the netcmd parsing in Part 5, so it rides with that.
- **Reduce the challenge expiry from sixty seconds.** Sixty is three times the reference's twenty (`uint16_t expire = 20` in the challenge-packet macro, `main.cpp:272-285`), which lengthens the window in which a client echoes something the proxy may not know. Task 1 removes the reason that matters, and the per-second repeat already makes delivery reliable.

---

# Part 5 — Its own project

- **Watermark, packet-type and network-command validation.** *Several sessions.* Recorded as a `TODO` in `UDPProxyService`. The reference's constant per-region watermark and dynamic CRC32C watermark are its highest-value anti-cheat signal and the highest false-positive risk in this whole area; adding them needs a region setting COMPEL has no equivalent for. It deserves its own spec and its own brainstorming rather than being appended here.

  **Where the definitions are.** `game_data_protocol.h` is not missing — it sits beside `main.cpp` and is the `GAME_CMD_*` table, which is what order-command validation needs. What is *not* anywhere in the reference tree is the packet flags and network commands `main.cpp` uses but never defines: `PACKET_NORMAL`, `PACKET_RELIABLE`, `PACKET_ACK`, `PACKET_PROXY` and `NETCMD_CLIENT_GAME_DATA` (0xc8) all come from the HON client's `src/k2/k2_protocol.h`. So this work ports from two trees, not one.

  **The `ClientPacketReader` minimum lengths of 43 and 41 are the reference's *floor*;** it additionally requires 44 for a normal packet and 47 for a reliable one by packet type (`main.cpp:576-580`). Its fourth condition, nominally a 48-byte minimum, is `data[PACKET_TYPE_POS] & PACKET_NORMAL & PACKET_RELIABLE` — and since those flags are `BIT(0)` and `BIT(1)`, that mask is zero and the branch can never fire. A reliable packet is already held to 47 by the preceding condition, so nothing is lost by it, but whoever ports this must decide deliberately whether to reproduce the dead branch, implement what it evidently intended, or leave it out.

---

# Final Verification

- [x] `dotnet build source/COMPEL.slnx` succeeds with 0 warnings.
- [x] `dotnet test source/COMPEL.slnx` passes, and every new test was proven to fail against the pre-fix code with the figures recorded.
- [x] `scripts/Publish-Native-AOT-Release.ps1` succeeds with no trim or AOT warnings, run per the note in Global Constraints.
- [x] **A real match through the proxy** shows `Disconnects(0)` and `proxyDroppedDatagramCount` of zero. Read it from `/status` with an `AuthenticationToken` set in `COMPEL.json` — without one the control plane refuses every request.
- [x] **Two sessions on one public port** — a second client, or a forced source-port change mid-match — with the drop count staying at zero. This is the condition the merge blocker needed, and no earlier verification exercised it.
- [x] **A COMPEL restart, then rejoining a match.** Note that a restart *necessarily ends any match in progress*: `MatchServerManagerSupervisor` kills orphaned manager processes at startup and before every launch, because spawned servers can be reparented and escape `Kill(entireProcessTree)`. The check is that a client can rejoin cleanly on a public port it used before, not that it plays through the restart.
- [x] A synthetic flood from many spoofed source endpoints does not exhaust sockets and does not refuse the players already in a match.
- [x] Forty datagrams spoofed as a player in a live match do not interrupt that player.

## Notes from the live verification that produced this list

- The test install's `COMPEL.json` predated four settings the current build expects (`WarmInstancesTarget`, `CDNSynchronisation`, `AuthenticationToken`, `ControlPlanePort`). COMPEL applied defaults and started cleanly, which is the right behaviour, but a stale `RuntimeArtefactsPath` of `TEMP` was rejected outright — the validator names the setting and the file, which made it a five-second fix.
- The manager's `console.log` fills with `No free ports found` at startup while the slaves are binding. All five slaves started and bound regardless, and the manager log showed `SLAVE_START` for all five followed by `SLAVE_INITIALIZED`. It appears to be a startup artefact rather than a fault, but it is noisy and was independently investigated earlier in this work, so it is worth confirming rather than assuming.
- COMPEL must run from a path containing whitespace or instances silently start as clients and never bind their game port. The start-up guard enforces this; the publish directory `windows-x64-native-aot` does **not** satisfy it, so live testing needs an install directory whose name contains a space.
