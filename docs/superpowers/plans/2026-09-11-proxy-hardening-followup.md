# Proxy Hardening Follow-up Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Address code review findings from proxy hostile traffic hardening: enforce strict timestamp monotonicity in `RotateChallenges`, eliminate exception-based control flow on the hot packet flood path, clean up stale comments and dead constants in `UDPProxyService`, align test naming with repo standards, and integrate `ClientChallengeStoreDouble` with `ForwarderProbe`.

**Architecture:**
- `UDPForwarder.RotateChallenges`: Advance session timestamps via `ClientSession.NextIssuedTimestamp()` to preserve per-session strict monotonicity against same-second rotations and clock adjustments.
- `UDPForwarder.TryGetOrCreateSession`: Refactor `GetOrCreateSession` from throwing `InvalidOperationException` to returning a `SessionAdmissionResult` enum, eliminating object allocations and stack unwinding during UDP floods.
- `UDPProxyService`: Purge stale TODO comments referencing completed hardening features and remove dead constants (`UnderAttackThreshold`, `UnderAttackWindowPasses`).
- Test Suite: Fix naming convention discrepancies in `UDPForwarderTests.cs` and `DatagramValidatorTests.cs`, and wire `ClientChallengeStoreDouble` into `ForwarderProbe` to validate real challenge datagram acceptance.

**Tech Stack:** .NET 11, C# 13, Native AOT, xUnit/TUnit-style assertion library.

## Global Constraints
- Never use `var`; use explicit type names.
- Acronyms and initialisms in camelCase must be uppercase unless at the start of the symbol (`clientOtherIP`, `otherIPDatagram`).
- PascalCase symbols must use uppercase for acronyms and initialisms (`ClientEndPoint`).
- Full variable names for delegates and lambdas; no abbreviations or single-letter names.
- British English spelling in all code and comments.
- StartCase for comments, sentence case for XML documentation.
- Zero trim or Native AOT warnings; 0 build warnings.
- Underscore between every word in test method names.

---

### Task 1: Repository Style & Naming Alignment

**Files:**
- Modify: `source/COMPEL.Tests/Services/Proxy/UDPForwarderTests.cs:617-621`
- Modify: `source/COMPEL.Tests/Services/Proxy/DatagramValidatorTests.cs:8, 20, 43, 65, 88, 114, 121, 149, 172, 195`

**Interfaces:**
- `UDPForwarderTests`: `clientOtherIP` and `otherIPDatagram`.
- `DatagramValidatorTests`: `ClientEndPoint`.

- [x] **Step 1: Fix camelCase acronym casing in `UDPForwarderTests.cs`**
  Rename `clientOtherIp` -> `clientOtherIP` and `otherIpDatagram` -> `otherIPDatagram` in `A_Forwarder_At_Its_Per_Address_Session_Cap_Refuses_A_Novel_Session_From_That_Address_While_Admitting_Another_Address`.

- [x] **Step 2: Fix PascalCase property casing in `DatagramValidatorTests.cs`**
  Rename `ClientEndpoint` -> `ClientEndPoint` across `DatagramValidatorTests.cs`.

- [x] **Step 3: Run test suite**
  Run: `dotnet test source/COMPEL.slnx`
  Expected: All tests pass, 0 warnings.

- [x] **Step 4: Commit**
  ```bash
  git add source/COMPEL.Tests/Services/Proxy/UDPForwarderTests.cs source/COMPEL.Tests/Services/Proxy/DatagramValidatorTests.cs
  git commit -m "Align Acronym Casing With Repository Style Guidelines"
  ```

---

### Task 2: Clean Up Stale TODO Comments and Dead Constants in `UDPProxyService`

**Files:**
- Modify: `source/COMPEL/Services/Proxy/UDPProxyService.cs:3-9, 21, 34, 37`

**Interfaces:**
- Consumes: `AttackIndicatorContainer.UnderAttackThreshold`
- Removes unused: `UDPProxyService.UnderAttackThreshold`, `UDPProxyService.UnderAttackWindowPasses`

- [x] **Step 1: Remove stale TODO comments**
  Remove lines 5–9 and line 21 in `UDPProxyService.cs` which refer to completed items (random challenges, session caps, spoofed source abuse, and the deleted hostile traffic plan).

- [x] **Step 2: Remove unused dead constants**
  Remove `private const int UnderAttackThreshold = 1000;` and `internal static readonly int UnderAttackWindowPasses = ...;` from `UDPProxyService.cs`.

- [x] **Step 3: Run build and test suite**
  Run: `dotnet build source/COMPEL.slnx`
  Expected: Succeeded with 0 warnings.

- [x] **Step 4: Commit**
  ```bash
  git add source/COMPEL/Services/Proxy/UDPProxyService.cs
  git commit -m "Clean Up Stale TODO Comments And Unused Attack Constants"
  ```

---

### Task 3: Enforce Strictly Monotonic Timestamps in `RotateChallenges`

**Files:**
- Modify: `source/COMPEL/Services/Proxy/UDPForwarder.cs:172-185`
- Test: `source/COMPEL.Tests/Services/Proxy/UDPForwarderTests.cs`

**Interfaces:**
- Consumes: `ClientSession.NextIssuedTimestamp()`
- Produces: Strictly monotonic timestamps across challenge rotations per session.

- [x] **Step 1: Write failing test in `UDPForwarderTests.cs`**
  Add `Rotate_Challenges_In_Same_Second_Produces_Strictly_Increasing_Timestamp_For_Session`:
  Create a session, record initial challenge timestamp. Run `RotateChallenges()` immediately without advancing the clock (same Unix second). Verify the emitted challenge packet timestamp is strictly greater than the initial timestamp.

- [x] **Step 2: Run test to verify it fails**
  Run: `dotnet test source/COMPEL.slnx`
  Expected: FAIL because raw `GetUtcNow().ToUnixTimeSeconds()` does not advance within the same second.

- [x] **Step 3: Update `RotateChallenges` implementation**
  In `UDPForwarder.RotateChallenges`:
  ```csharp
  uint sequence = NextChallengeSequence();
  challenges.Rotate(sequence, packetQuota);

  foreach (KeyValuePair<IPEndPoint, ClientSession> pair in sessions)
  {
      uint issuedTimestamp = pair.Value.NextIssuedTimestamp();
      Volatile.Write(ref pair.Value.IssuedTimestamp, issuedTimestamp);
      TransmitChallenge(pair.Key, sequence, issuedTimestamp);
  }
  ```

- [x] **Step 4: Run tests to verify pass**
  Run: `dotnet test source/COMPEL.slnx`
  Expected: PASS.

- [x] **Step 5: Commit**
  ```bash
  git add source/COMPEL/Services/Proxy/UDPForwarder.cs source/COMPEL.Tests/Services/Proxy/UDPForwarderTests.cs
  git commit -m "Enforce Strictly Monotonic Challenge Timestamps On Rotation"
  ```

---

### Task 4: Replace Exception Throwing with Non-Allocating Flow Control in `GetOrCreateSession`

**Files:**
- Modify: `source/COMPEL/Services/Proxy/UDPForwarder.cs:110-121, 298-356`

**Interfaces:**
- Introduces: `internal enum SessionAdmissionResult { Admitted, UnderAttack, ForwarderCapReached, AddressCapReached, CreationFailed }`
- Modifies: `TryGetOrCreateSession(IPEndPoint client, CancellationToken stoppingToken, out ClientSession? session, out bool created)`

- [x] **Step 1: Introduce `SessionAdmissionResult` enum**
  Define `SessionAdmissionResult` inside `UDPForwarder`.

- [x] **Step 2: Refactor `GetOrCreateSession` to `TryGetOrCreateSession`**
  Instead of throwing `InvalidOperationException`:
  - Return `SessionAdmissionResult.UnderAttack` if `attackIndicator.IsUnderAttack`.
  - Charge `CapRefusalAttackWeight` and return `SessionAdmissionResult.ForwarderCapReached` if `sessions.Count >= MaxSessionsPerForwarder`.
  - Charge `CapRefusalAttackWeight` and return `SessionAdmissionResult.AddressCapReached` if `sessionCountForAddress >= MaxSessionsPerAddress`.
  - Catch any unexpected `SocketException` during socket creation and return `SessionAdmissionResult.CreationFailed`.
  - On success, charge `NovelEndpointAttackWeight`, start pump, set `created = true`, and return `SessionAdmissionResult.Admitted`.

- [x] **Step 3: Update `Run` receive loop**
  Replace `try { session = GetOrCreateSession(...) } catch` with:
  ```csharp
  SessionAdmissionResult admissionResult = TryGetOrCreateSession(client, stoppingToken, out ClientSession? session, out bool created);

  if (admissionResult is not SessionAdmissionResult.Admitted)
  {
      scoreContainer.ChargeArrival(client);

      if (Drop(client, "Session Creation Failed"))
          logger.LogDebug("Failed To Create Proxy Session For {Client} ({Reason})", client, admissionResult);

      continue;
  }
  ```

- [x] **Step 4: Run test suite**
  Run: `dotnet test source/COMPEL.slnx`
  Expected: PASS (all cap refusal, attack refusal, and eviction tests pass with zero exception overhead).

- [x] **Step 5: Commit**
  ```bash
  git add source/COMPEL/Services/Proxy/UDPForwarder.cs
  git commit -m "Eliminate Exception Allocation For Session Creation Rejection"
  ```

---

### Task 5: Integrate `ClientChallengeStoreDouble` with `ForwarderProbe`

**Files:**
- Modify: `source/COMPEL.Tests/Services/Proxy/UDPForwarderTests.cs`

**Interfaces:**
- Consumes: `ClientChallengeStoreDouble`
- Modifies: `ForwarderProbe`

- [x] **Step 1: Add `ClientChallengeStoreDouble` property to `ForwarderProbe`**
  Expose `internal ClientChallengeStoreDouble ClientChallenges { get; } = new ();` on `ForwarderProbe`.
  In `ForwarderProbe.TryReceiveChallenge()` or receive pump, pass received challenge packets to `ClientChallenges.TryProcessChallengePacket(publicEndPoint, buffer)`.

- [x] **Step 2: Add integration test verifying end-to-end challenge acceptance**
  Add test `Rotated_And_Repeated_Challenges_Are_Accepted_By_Client_Challenge_Store`:
  Establish a session through probe, verify `ClientChallenges` accepted the challenge.
  Trigger `Forwarder.RotateChallenges()`, verify `ClientChallenges` processed the replacement challenge and updated the held challenge value.
  Trigger `Forwarder.RepeatChallenges()`, verify the repeat packet is processed without error.

- [x] **Step 3: Run test suite**
  Run: `dotnet test source/COMPEL.slnx`
  Expected: PASS.

- [x] **Step 4: Commit**
  ```bash
  git add source/COMPEL.Tests/Services/Proxy/UDPForwarderTests.cs
  git commit -m "Integrate Client Challenge Store Double Into Forwarder Probe"
  ```

---

### Task 6: Native AOT Compilation & End-to-End Verification

**Files:**
- No code changes.

- [ ] **Step 1: Run full test suite**
  Run: `dotnet test source/COMPEL.slnx`
  Expected: All 170+ tests pass with 0 failures.

- [ ] **Step 2: Run Native AOT publish script**
  Run: `pwsh scripts/Publish-Native-AOT-Release.ps1`
  Expected: Clean publish with 0 warnings.
