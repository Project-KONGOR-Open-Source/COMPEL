# Proxy Hostile-Traffic Hardening Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close the two ways a hostile source can cheaply harm COMPEL's proxy or a named player, neither of which the abuse-protection work addressed.

**Architecture:** Three of the four tasks are small and self-contained — a cap before session creation, a live under-attack indicator, and an unpredictable challenge value. The fourth changes what the violation score is allowed to *decide*, and needs its design confirmed before implementation rather than treated as settled.

**Tech Stack:** .NET 11, ASP.NET Core with Native AOT, TUnit on the Microsoft Testing Platform.

**Spec:** `docs/superpowers/specs/2026-09-10-proxy-abuse-protection-design.md`, with the deferred items and their reasoning in `docs/superpowers/specs/2026-09-11-proxy-abuse-protection-follow-up.md` (items 1, 2, 5 and 8 are what this plan implements).

**Why these four and not the other six.** The deferred document lists ten items. These four are the ones where *doing nothing* leaves a hole a hostile source can walk through today. The rest are either testability and maintainability work recorded as code TODOs, deliberately skipped with a reason, or a project of their own (watermark validation).

## Global Constraints

- Reference implementation: `source/COMPEL/bin/Publish/HoN_Proxy/HoN/branches/retail/Tool/HoNProxy/main.cpp`. The HON client is at `C:/Users/SADS-810/Source/HON/src/k2/c_enhanced_watermark.cpp` — read it for anything about what a client will accept, and do not trust any document's quotation of either.
- **Every task starts by re-reading its cited reference lines.** Five reviews of the previous work each corrected a documented claim about the reference. Assume this document is wrong about it until checked.
- The datagram pipeline's ordering is a security property: charge the arrival, refuse an actioned source, length-guard, read the fields, match the challenge, refuse an unmatched one, admit the counter once, relay. Task 4 deliberately changes what "refuse an actioned source" means; nothing else reorders it.
- The datagram path must not await and must not allocate per datagram.
- Never use `var`; explicit type names throughout.
- Acronyms and initialisms upper-case in PascalCase (`IPEndPoint`, `UDPForwarder`), in camelCase only when not leading.
- Full words, never abbreviations. British English. Four spaces. Comments in StartCase; XML summaries in sentence case with a full stop per sentence and no sentence split across lines. Symbol references in double quotation marks or a parameterless `<see cref="..."/>`. Never the null-forgiving operator.
- A constant ported from the reference carries its `#define` name in a trailing comment.
- `dotnet build source/COMPEL.slnx` reports 0 warnings after every task.
- **A test must fail against the pre-fix code.** Prove it by stashing the production change and running the test, and record the figures. Three tests on the previous branch appeared to pin an invariant and did not.

---

### Task 1: Cap Sessions Per Forwarder And Per Source Address

**Files:**
- Modify: `source/COMPEL/Services/Proxy/UDPForwarder.cs`
- Test: `source/COMPEL.Tests/Services/Proxy/UDPForwarderTests.cs`

**The hole.** `GetOrCreateSession` runs before the length guard and before the allowance check, so one datagram from any novel source endpoint buys a UDP socket, a pump task with a 65,535-byte buffer, a linked `CancellationTokenSource`, a `SessionChallengeState`, and a challenge datagram to whatever address it named — with no cap on how many such sessions exist. A spoofed-source flood exhausts sockets and file descriptors, and the abuse score cannot help, because it is keyed on the endpoint and every spoofed endpoint is new.

The reference caps this at `MAX_GAME_CONNECTIONS` 24 and `MAX_GAME_CONNECTIONS_PER_IP` 10 (`main.cpp:53-56`), enforced before a connection is admitted (`:1718`, `:1739`).

**The decision this task needs first.** COMPEL runs one forwarder per instance per kind, and the instance count is configurable. Twenty-four per forwarder matches the reference and a full HoN match (ten players plus spectators), but confirm against `MatchServerManagerOptions` whether a larger figure is wanted for a busy host, and whether the per-address cap should be ten as the reference has it. Write the chosen figures into the constants with the reference names beside them.

- [ ] **Step 1: Fact verification.** Confirm `MAX_GAME_CONNECTIONS`, `MAX_VOICE_CONNECTIONS`, `MAX_GAME_CONNECTIONS_PER_IP` and `MAX_VOICE_CONNECTIONS_PER_IP`, and read the two enforcement sites to confirm both are checked *before* the socket is created.
- [ ] **Step 2: Write the failing tests.** A forwarder at its session cap refuses a novel endpoint without creating a session; a source address at its per-address cap is refused while a different address is admitted; and a session freed by eviction releases its slot so the cap is not a permanent ceiling.
- [ ] **Step 3: See them fail.**
- [ ] **Step 4: Implement.** Check both caps in `GetOrCreateSession` before constructing anything, and return without a session. Count live sessions per `IPAddress` in a `ConcurrentDictionary<IPAddress, int>` maintained alongside `sessions`, decremented wherever a session dies — the same three sites `ReleaseDropReport` is called from. A refused datagram takes the unweighted `Drop` so it is counted and throttled but not scored: being the twenty-fifth player is not the client's fault.
- [ ] **Step 5: See them pass, and commit.**

---

### Task 2: Make The Under-Attack Indicator Live

**Files:**
- Modify: `source/COMPEL/Services/Proxy/UDPProxyService.cs`, `source/COMPEL/Services/Proxy/UDPForwarder.cs`
- Test: `source/COMPEL.Tests/Services/Proxy/UDPProxyServiceTests.cs`

**Why it has to change before it can be used.** `IsUnderAttack` is recomputed only when a five-minute window closes, so a flood is invisible for up to five minutes and the flag stays set for up to five minutes after one ends. Task 1's cap is a fixed ceiling; the reference additionally refuses *all* new connections while its indicator is over threshold (`main.cpp:1041`), which is what lets it survive a flood that stays under the per-address cap by spreading across addresses. Gating admission on the current indicator would refuse every new player for five minutes after an attack ended.

- [ ] **Step 1: Fact verification.** Read every `under_attack_indicator` site and confirm it is weighted (+1 per unknown-address datagram, +100 per blocked connection, +10000 on ban-table saturation) and read per datagram rather than per window.
- [ ] **Step 2: Write the failing tests.** The indicator rises within one maintenance pass of a burst rather than one window; it falls again once the burst stops; and a refused-by-cap datagram contributes more than an ordinary refusal.
- [ ] **Step 3: See them fail.**
- [ ] **Step 4: Implement.** Replace the window-delta computation with a decaying weighted counter updated on the drop path, using the same elapsed-proportional drain `ViolationScoreContainer` uses so the mechanism is already familiar and already tested. Keep `UnderAttackThreshold` at the reference's 1000 and weight the events as the reference does.
- [ ] **Step 5: Gate admission.** Refuse a novel endpoint while the indicator is over threshold, as the reference does. An existing session is unaffected, so a match in progress is never interrupted by this.
- [ ] **Step 6: See them pass, and commit.**

---

### Task 3: Make The Challenge Value Unpredictable

**Files:**
- Modify: `source/COMPEL/Services/Proxy/UDPForwarder.cs`
- Test: `source/COMPEL.Tests/Services/Proxy/UDPForwarderTests.cs`

**Why this is now cheap.** The challenge value was a monotonic counter because it doubled as the server-creation timestamp. That coupling was removed during the previous work, so this is a self-contained change — and it is worth doing because a per-forwarder counter issues *consecutive* values to co-located sessions, so a client holding value 17 can infer its co-players hold roughly `{11…20}`. That is what lets one player inject traffic into a match as another.

- [ ] **Step 1: Fact verification.** Confirm the reference draws from a CSPRNG and retries against its whole retained list (`main.cpp:1223`, `:1227`), and confirm the precondition already documented on `SessionChallengeState.Rotate`: a value equal to one still retained must never be reissued, because it would build a fresh window and discard counters the client has already consumed.
- [ ] **Step 2: Write the failing tests.** Two successive challenges are not consecutive; a value is never zero, because zero marks an unauthenticated client; and a value already retained by the session is not reissued.
- [ ] **Step 3: See them fail.**
- [ ] **Step 4: Implement.** Draw from `RandomNumberGenerator`, reject zero, and retry against the session's retained values. `SessionChallengeState` needs a way to ask whether it retains a value — add one rather than reaching into its list.
- [ ] **Step 5: See them pass, and commit.**

---

### Task 4: Stop A Spoofed Source Silencing A Player

**This task needs its design confirmed before implementation. Do not start it as a transcription task.**

**Files:**
- Modify: `source/COMPEL/Services/Proxy/ViolationScoreContainer.cs`, `source/COMPEL/Services/Proxy/UDPForwarder.cs`
- Test: `source/COMPEL.Tests/Services/Proxy/ViolationScoreContainerTests.cs`, `source/COMPEL.Tests/Services/Proxy/UDPForwarderTests.cs`

**The hole.** Scoring is keyed on `IPEndPoint` and UDP source addresses are forgeable. An unmatched challenge costs 100 against a threshold of 4000, so roughly **forty spoofed datagrams action a named player**, after which their own traffic is refused until the score drains. Repeating forty datagrams every few seconds sustains it at no measurable cost. This is the one thing in the abuse protection that is worse than the transparent relay it replaced, which had no score to drive.

**Why the reference is no help.** It has the same exposure — `warns` is keyed on address and port too — and its response is worse: a firewall ban on the whole IP, which also removes anyone sharing it. COMPEL cannot copy its way out of this one.

**The proposed design, to be confirmed.** Separate what the score *records* from what it is allowed to *decide*.

Today one number both accumulates violations and gates the drop. The proposal is to action a source on its **sustained arrival rate** alone — the per-packet cost against the drain, which is already implemented and already tested — and let violation weights accumulate for logging, the under-attack indicator, and diagnosis without gating the drop. The consequence is that silencing a player requires actually sustaining more than `EstimatedPacketsPerSecond` spoofed as them, which costs the attacker real bandwidth and is no cheaper than attacking that player directly. The forty-packet asymmetry disappears.

What this gives up: an invalid-traffic source is no longer *actioned*, so each of its datagrams is validated and dropped individually rather than short-circuited at the allowance check. That is a small cost on the flood path, and the arrival-rate component still actions anything sending fast enough to matter.

**Alternatives considered, recorded so they are not rediscovered.** Capping what unmatched-challenge violations alone may contribute is simpler but arbitrary, and leaves a smaller version of the same asymmetry. Exempting a source whose current datagram validates would protect the victim perfectly but makes the actioned state gate nothing at all, since valid traffic would always relay. Doing nothing is defensible only if the proxy is never exposed to a hostile player, which is not the deployment.

- [ ] **Step 1: Confirm the design** with the repository owner before writing code, presenting the proposal and the two alternatives. If it is rejected, stop and re-plan rather than implementing a compromise.
- [ ] **Step 2 onwards:** to be written once the design is confirmed, following the usual write-the-failing-test-first cycle. The test that matters is the one that proves forty spoofed datagrams no longer refuse a victim's own valid traffic.

---

## Final Verification

- [ ] `dotnet build source/COMPEL.slnx` succeeds with 0 warnings.
- [ ] `dotnet test source/COMPEL.slnx` passes, and every new test was proven to fail against the pre-fix code with the figures recorded.
- [ ] `scripts/Publish-Native-AOT-Release.ps1` succeeds with no trim or AOT warnings. Run it with `pwsh`, redirect to a file, and read the script's own exit code — `powershell` on this machine is 5.1 and the script requires 7, and a piped `grep` will hide that it refused to run at all.
- [ ] A real match through the proxy shows `Disconnects(0)` and a drop count of zero.
- [ ] A synthetic flood from many spoofed source endpoints does not exhaust sockets, and does not refuse the players already in a match.
- [ ] Forty datagrams spoofed as a player in a live match do not interrupt that player.
