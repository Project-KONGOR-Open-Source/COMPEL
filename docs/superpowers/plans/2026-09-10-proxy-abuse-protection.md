# Proxy Abuse Protection Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Validate, rate limit and score incoming client datagrams in COMPEL's managed proxy, so a client cannot flood or malform its way through to a match server.

**Architecture:** Each datagram passes an ordered pipeline before it is relayed: a length guard, then a read of the client-supplied challenge and counter, then per-challenge quota and duplicate checks, then a per-source decaying violation score. The score container owns one framework token limiter per source, replenished explicitly by the proxy's existing maintenance loop; everything else is a small pure unit with its own tests. Nothing in the pipeline awaits.

**Tech Stack:** .NET 11, ASP.NET Core with Native AOT, `System.Threading.RateLimiting` (already in the shared framework), TUnit on the Microsoft Testing Platform.

**Spec:** `docs/superpowers/specs/2026-09-10-proxy-abuse-protection-design.md`

## Global Constraints

- **Every task starts with its Fact Verification step.** Do not write code for a task until its cited reference lines have been re-read and confirmed. If a citation does not say what the plan claims, stop and report rather than proceeding.
- Reference implementation: `source/COMPEL/bin/Publish/HoN_Proxy/HoN/branches/retail/Tool/HoNProxy/main.cpp`.
- **Ordering is a safety property:** the quota check must precede the duplicate check. The counter is client-controlled up to 65535 while the duplicate bitmap is sized to the quota, so the reverse order indexes out of bounds.
- **No violation weight may exceed `ActionableThreshold`.** A permit count above the container limit throws rather than failing to acquire.
- Only synchronous `AttemptAcquire` with `QueueLimit = 0`. The datagram loop must never await a limiter.
- Never use `var`; always explicit type names.
- Acronyms and initialisms upper-case in PascalCase (`UDPForwarder`, `IPEndPoint`); in camelCase only when not leading.
- Full words, never abbreviations: `configuration`, `maximum`, `duplicate`, `command`.
- British English throughout code and comments.
- Four spaces for indentation; CRLF line endings; every file ends with a newline.
- Comments in StartCase; XML summaries in sentence case with terminating full stops.
- Symbol references in comments in double quotation marks, or a `<see cref="..."/>` tag without parameters.
- Never use the null-forgiving operator.
- Global using directives live in `source/COMPEL/Internals/UsingDirectives.cs`, lexicographic within assembly groups.
- Test method names separate every word or logical number sequence with underscores.
- `dotnet build source/COMPEL.slnx` must report 0 warnings after every task.

---

### Task 1: Forwarder Kind And Challenge Quota Derivation

**Files:**
- Create: `source/COMPEL/Services/Proxy/ProxyForwarderKind.cs`
- Create: `source/COMPEL/Services/Proxy/ChallengeQuota.cs`
- Test: `source/COMPEL.Tests/Services/Proxy/ChallengeQuotaTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `enum ProxyForwarderKind { Game, Voice }`; `ChallengeQuota.GamePacketsPerSecond`, `.GameCommandPacketsPerSecond`, `.VoicePacketsPerSecond`, `.UnauthenticatedPacketQuota` (all `const`); `static ushort ChallengeQuota.Derive(int ratePerSecond, TimeSpan renewalInterval)`; `static ushort ChallengeQuota.ForKind(ProxyForwarderKind kind, TimeSpan renewalInterval)`; `static ushort ChallengeQuota.GameCommandForInterval(TimeSpan renewalInterval)`.

- [ ] **Step 1: Fact verification**

Run and confirm each value before writing any code:

```bash
cd "source/COMPEL/bin/Publish/HoN_Proxy/HoN/branches/retail/Tool/HoNProxy"
grep -nE "define (CHALLENGE_REFRESH_TIME|CHALLENGE_MAX_CTR|CHALLENGE_MAX_GAME_CMD_CTR|CHALLENGE_MAX_CTR_VOICE|MAX_CTR_UNAUTHENTICATED)" main.cpp
grep -n "initializeChallengePackage(game" main.cpp
```

Expected: refresh 5 seconds; `CHALLENGE_MAX_CTR 720`; `CHALLENGE_MAX_GAME_CMD_CTR 40`; `CHALLENGE_MAX_CTR_VOICE 50`; `MAX_CTR_UNAUTHENTICATED 100`; and line 986 selecting the maximum by kind. The rates are those counters divided by the 5-second refresh: 144, 8 and 10 per second.

- [ ] **Step 2: Write the failing tests**

```csharp
namespace COMPEL.Tests.Services.Proxy;

/// <summary>
///     Verifies that the advertised and enforced packet quotas are derived from one rate, so they cannot diverge.
/// </summary>
public sealed class ChallengeQuotaTests
{
    [Test]
    public async Task The_Quota_Is_The_Rate_Multiplied_By_The_Renewal_Interval()
    {
        using (Assert.Multiple())
        {
            await Assert.That(ChallengeQuota.Derive(144, TimeSpan.FromSeconds(5))).IsEqualTo((ushort)720);
            await Assert.That(ChallengeQuota.Derive(144, TimeSpan.FromSeconds(10))).IsEqualTo((ushort)1440);
        }
    }

    // The Reference Grants 720 Packets Over A Five-Second Refresh, So A Five-Second Interval Must Reproduce The Reference Exactly
    [Test]
    public async Task A_Five_Second_Interval_Reproduces_The_Reference_Quotas()
    {
        TimeSpan referenceRefresh = TimeSpan.FromSeconds(5);

        using (Assert.Multiple())
        {
            await Assert.That(ChallengeQuota.ForKind(ProxyForwarderKind.Game, referenceRefresh)).IsEqualTo((ushort)720);
            await Assert.That(ChallengeQuota.ForKind(ProxyForwarderKind.Voice, referenceRefresh)).IsEqualTo((ushort)50);
            await Assert.That(ChallengeQuota.GameCommandForInterval(referenceRefresh)).IsEqualTo((ushort)40);
        }
    }

    [Test]
    public async Task Voice_Is_Quoted_Far_Lower_Than_Game()
    {
        TimeSpan interval = TimeSpan.FromSeconds(10);

        await Assert.That(ChallengeQuota.ForKind(ProxyForwarderKind.Voice, interval))
            .IsLessThan(ChallengeQuota.ForKind(ProxyForwarderKind.Game, interval));
    }

    // A Long Interval Must Not Wrap The Unsigned Sixteen-Bit Field The Quota Is Advertised In
    [Test]
    public async Task An_Interval_Large_Enough_To_Overflow_Is_Clamped()
    {
        await Assert.That(ChallengeQuota.Derive(144, TimeSpan.FromHours(1))).IsEqualTo(ushort.MaxValue);
    }

    [Test]
    public async Task No_Rate_Is_Zero_Or_Negative()
    {
        using (Assert.Multiple())
        {
            await Assert.That(ChallengeQuota.GamePacketsPerSecond).IsGreaterThan(0);
            await Assert.That(ChallengeQuota.GameCommandPacketsPerSecond).IsGreaterThan(0);
            await Assert.That(ChallengeQuota.VoicePacketsPerSecond).IsGreaterThan(0);
        }
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet build source/COMPEL.slnx`
Expected: FAIL with `CS0103`/`CS0246` — `ChallengeQuota` and `ProxyForwarderKind` do not exist.

- [ ] **Step 4: Create the forwarder kind**

```csharp
namespace COMPEL.Services.Proxy;

/// <summary>
///     Which traffic a forwarder relays. The two kinds are quoted differently, because the reference proxy grants a voice client far fewer packets than a game client.
/// </summary>
internal enum ProxyForwarderKind
{
    Game,
    Voice
}
```

- [ ] **Step 5: Create the quota derivation**

```csharp
namespace COMPEL.Services.Proxy;

/// <summary>
///     The per-challenge packet quotas the proxy advertises to a client and enforces against it.
///     Only the rates are stated; the quota itself is derived from the rate and the challenge renewal interval, so the advertised and enforced values cannot drift apart when the interval changes.
/// </summary>
internal static class ChallengeQuota
{
    // "CHALLENGE_MAX_CTR" (720) Over The Reference's Five-Second "CHALLENGE_REFRESH_TIME"
    internal const int GamePacketsPerSecond = 144;

    // "CHALLENGE_MAX_GAME_CMD_CTR" (40) Over The Reference's Five-Second Refresh
    internal const int GameCommandPacketsPerSecond = 8;

    // "CHALLENGE_MAX_CTR_VOICE" (50) Over The Reference's Five-Second Refresh
    internal const int VoicePacketsPerSecond = 10;

    // "MAX_CTR_UNAUTHENTICATED": A Total For The Whole Unauthenticated State Rather Than A Rate, So It Is Not Derived
    internal const ushort UnauthenticatedPacketQuota = 100;

    /// <summary>
    ///     The number of packets a client may send within one challenge at the supplied rate, clamped to the unsigned sixteen-bit field the quota is advertised in.
    /// </summary>
    internal static ushort Derive(int ratePerSecond, TimeSpan renewalInterval)
    {
        double quota = ratePerSecond * renewalInterval.TotalSeconds;

        return quota >= ushort.MaxValue ? ushort.MaxValue : (ushort)Math.Ceiling(quota);
    }

    /// <summary>
    ///     The total packet quota for the supplied forwarder kind.
    /// </summary>
    internal static ushort ForKind(ProxyForwarderKind kind, TimeSpan renewalInterval) => kind switch
    {
        ProxyForwarderKind.Voice => Derive(VoicePacketsPerSecond, renewalInterval),
        ProxyForwarderKind.Game  => Derive(GamePacketsPerSecond, renewalInterval),
        _                        => throw new ArgumentOutOfRangeException(nameof(kind), @$"Unsupported Forwarder Kind ""{kind}""")
    };

    /// <summary>
    ///     The game command packet quota, which the reference advertises separately from the total.
    /// </summary>
    internal static ushort GameCommandForInterval(TimeSpan renewalInterval) => Derive(GameCommandPacketsPerSecond, renewalInterval);
}
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet build source/COMPEL.slnx && dotnet test source/COMPEL.slnx`
Expected: build succeeds with 0 warnings; all tests pass, including the five new ones.

- [ ] **Step 7: Commit**

```bash
git add source/COMPEL/Services/Proxy/ProxyForwarderKind.cs source/COMPEL/Services/Proxy/ChallengeQuota.cs source/COMPEL.Tests/Services/Proxy/ChallengeQuotaTests.cs
git commit -m "Derive The Proxy Challenge Packet Quotas From One Rate"
```

---

### Task 2: Client Packet Reader

**Files:**
- Create: `source/COMPEL/Services/Proxy/ClientPacketReader.cs`
- Test: `source/COMPEL.Tests/Services/Proxy/ClientPacketReaderTests.cs`

**Interfaces:**
- Consumes: `ProxyForwarderKind` from Task 1.
- Produces: `static int ClientPacketReader.MinimumLength(ProxyForwarderKind kind)`; `static bool ClientPacketReader.TryRead(ReadOnlySpan<byte> datagram, ProxyForwarderKind kind, out uint challenge, out ushort counter)`.

- [ ] **Step 1: Fact verification**

```bash
cd "source/COMPEL/bin/Publish/HoN_Proxy/HoN/branches/retail/Tool/HoNProxy"
sed -n '570,576p' main.cpp
sed -n '631,635p' main.cpp
```

Expected: the length guard rejecting voice below 41 bytes and game below 43; and the field reads placing the challenge at offset 28 as a four-byte value and the counter at offset 32 as a two-byte value. Confirm the counter offset is 32 before writing the reader — the whole task depends on it.

- [ ] **Step 2: Write the failing tests**

```csharp
namespace COMPEL.Tests.Services.Proxy;

/// <summary>
///     Verifies the length guard and the field offsets used to read a client datagram, both taken from the reference proxy.
/// </summary>
public sealed class ClientPacketReaderTests
{
    private static byte[] BuildDatagram(uint challenge, ushort counter, int length)
    {
        byte[] datagram = new byte[length];

        BinaryPrimitives.WriteUInt32LittleEndian(datagram.AsSpan(28), challenge);
        BinaryPrimitives.WriteUInt16LittleEndian(datagram.AsSpan(32), counter);

        return datagram;
    }

    [Test]
    public async Task The_Minimum_Length_Differs_By_Kind()
    {
        using (Assert.Multiple())
        {
            await Assert.That(ClientPacketReader.MinimumLength(ProxyForwarderKind.Game)).IsEqualTo(43);
            await Assert.That(ClientPacketReader.MinimumLength(ProxyForwarderKind.Voice)).IsEqualTo(41);
        }
    }

    [Test]
    public async Task The_Challenge_And_Counter_Are_Read_From_Their_Reference_Offsets()
    {
        byte[] datagram = BuildDatagram(challenge: 0x11223344, counter: 4321, length: 64);

        bool read = ClientPacketReader.TryRead(datagram, ProxyForwarderKind.Game, out uint challenge, out ushort counter);

        using (Assert.Multiple())
        {
            await Assert.That(read).IsTrue();
            await Assert.That(challenge).IsEqualTo(0x11223344u);
            await Assert.That(counter).IsEqualTo((ushort)4321);
        }
    }

    // The Guard Exists So No Field Is Ever Read Out Of Bounds: A Datagram One Byte Short Of The Minimum Must Be Refused Outright
    [Test]
    public async Task A_Datagram_One_Byte_Below_The_Minimum_Is_Refused()
    {
        foreach (ProxyForwarderKind kind in (ProxyForwarderKind[])[ ProxyForwarderKind.Game, ProxyForwarderKind.Voice ])
        {
            byte[] datagram = new byte[ClientPacketReader.MinimumLength(kind) - 1];

            bool read = ClientPacketReader.TryRead(datagram, kind, out uint challenge, out ushort counter);

            using (Assert.Multiple())
            {
                await Assert.That(read).IsFalse();
                await Assert.That(challenge).IsEqualTo(0u);
                await Assert.That(counter).IsEqualTo((ushort)0);
            }
        }
    }

    [Test]
    public async Task An_Empty_Datagram_Is_Refused_Without_Throwing()
    {
        bool read = ClientPacketReader.TryRead([], ProxyForwarderKind.Game, out _, out _);

        await Assert.That(read).IsFalse();
    }

    [Test]
    public async Task A_Datagram_Exactly_At_The_Minimum_Is_Accepted()
    {
        byte[] datagram = BuildDatagram(challenge: 7, counter: 9, length: ClientPacketReader.MinimumLength(ProxyForwarderKind.Game));

        bool read = ClientPacketReader.TryRead(datagram, ProxyForwarderKind.Game, out uint challenge, out ushort counter);

        using (Assert.Multiple())
        {
            await Assert.That(read).IsTrue();
            await Assert.That(challenge).IsEqualTo(7u);
            await Assert.That(counter).IsEqualTo((ushort)9);
        }
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet build source/COMPEL.slnx`
Expected: FAIL — `ClientPacketReader` does not exist.

- [ ] **Step 4: Write the reader**

```csharp
namespace COMPEL.Services.Proxy;

/// <summary>
///     Reads the fields the proxy validates out of a client datagram, and refuses any datagram too short to contain them.
///     The offsets and the per-kind minimum lengths are those of the reference proxy, which drops a datagram before touching any field so that a short one cannot cause an out-of-range read.
/// </summary>
internal static class ClientPacketReader
{
    // The Watermark Prefix Occupies The Leading Forty Bytes; The Fields Below Sit Within Its Enhanced Half
    private const int ChallengeOffset = 28;
    private const int CounterOffset = 32;

    // "40 bytes Watermark + 2 bytes connection + 1 bytes packet type" For Game Traffic, Two Fewer For Voice
    private const int GameMinimumLength = 43;
    private const int VoiceMinimumLength = 41;

    /// <summary>
    ///     The shortest datagram of the supplied kind the proxy will consider.
    /// </summary>
    internal static int MinimumLength(ProxyForwarderKind kind) => kind switch
    {
        ProxyForwarderKind.Voice => VoiceMinimumLength,
        ProxyForwarderKind.Game  => GameMinimumLength,
        _                        => throw new ArgumentOutOfRangeException(nameof(kind), @$"Unsupported Forwarder Kind ""{kind}""")
    };

    /// <summary>
    ///     Reads the challenge the client echoed and the counter it stamped into this datagram.
    ///     Returns <see langword="false"/> without reading anything when the datagram is shorter than the minimum for its kind.
    /// </summary>
    internal static bool TryRead(ReadOnlySpan<byte> datagram, ProxyForwarderKind kind, out uint challenge, out ushort counter)
    {
        challenge = 0;
        counter = 0;

        if (datagram.Length < MinimumLength(kind))
            return false;

        challenge = BinaryPrimitives.ReadUInt32LittleEndian(datagram[ChallengeOffset..]);
        counter = BinaryPrimitives.ReadUInt16LittleEndian(datagram[CounterOffset..]);

        return true;
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet build source/COMPEL.slnx && dotnet test source/COMPEL.slnx`
Expected: build succeeds with 0 warnings; all tests pass.

- [ ] **Step 6: Commit**

```bash
git add source/COMPEL/Services/Proxy/ClientPacketReader.cs source/COMPEL.Tests/Services/Proxy/ClientPacketReaderTests.cs
git commit -m "Read The Challenge And Counter From A Client Datagram"
```

---

### Task 3: Violation Score Container

**Files:**
- Create: `source/COMPEL/Services/Proxy/ViolationScoreContainer.cs`
- Modify: `source/COMPEL/Internals/UsingDirectives.cs`
- Test: `source/COMPEL.Tests/Services/Proxy/ViolationScoreContainerTests.cs`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces: `ViolationScoreContainer` with a parameterless constructor, `void Charge(IPEndPoint source, int weight)`, `bool IsWithinAllowance(IPEndPoint source)`, `void Replenish()`, `void Dispose()`, and the constants `ActionableThreshold`, `EstimatedPacketsPerSecond`, `TooShortViolationWeight`, `UnauthenticatedViolationWeight`, `RateLimitViolationWeight`, `DuplicateViolationWeight`.

**API constraint, verified against the reference assembly:** `PartitionedRateLimiter.Create` has **no** `TimeProvider` overload, and the partitioned wrapper exposes no way to drive replenishment. The container therefore owns one `TokenBucketRateLimiter` per source with `AutoReplenishment = false`, and `Replenish()` is called once per second by the proxy service's existing maintenance loop. This matches the reference's explicit one-second housekeeping pass and makes the drain deterministically testable without waiting on wall-clock time.

`IsWithinAllowance` reads `GetStatistics().CurrentAvailablePermits` and consumes nothing. It exists because `AttemptAcquire` with a permit count of zero always succeeds, so a zero-weight charge could not have been used to test the threshold.

- [ ] **Step 1: Fact verification**

```bash
cd "source/COMPEL/bin/Publish/HoN_Proxy/HoN/branches/retail/Tool/HoNProxy"
grep -nE "define (BAN_THRESHOLD|MAX_WARN_COUNT|ESTIMATED_PACKETS_PER_SECOND|WARN_TOO_SHORT|WARN_UNAUTHENTICATED|WARN_LIMIT|WARN_DUPE)" main.cpp
sed -n '1183,1193p' main.cpp
```

Expected: threshold 4000; maximum 20000; drain 140 per second; weights 200 (too short), 200 (unauthenticated), 100 (rate limit), 30 (duplicate); and the decay loop subtracting `elapsed_milliseconds * ESTIMATED_PACKETS_PER_SECOND / 1000.0f`. Confirm every weight is below the threshold, because a weight above it would throw on the datagram path.

- [ ] **Step 2: Add the global using directive**

In `source/COMPEL/Internals/UsingDirectives.cs`, insert in lexicographic order within the existing `System` group, immediately after the `global using System.Text.Json.Serialization;` line if present, otherwise after the last `System.T*` line:

```csharp
global using System.Threading.RateLimiting;
```

- [ ] **Step 3: Write the failing tests**

Replenishment is explicit, so the drain is driven by calling `Replenish()` rather than by waiting.

```csharp
namespace COMPEL.Tests.Services.Proxy;

/// <summary>
///     Verifies the per-source violation score: that ordinary traffic never reaches the actionable threshold, that abusive traffic does, and that a source recovers as its score drains.
/// </summary>
public sealed class ViolationScoreContainerTests
{
    private static IPEndPoint Source(int port = 40000) => new (IPAddress.Parse("203.0.113.5"), port);

    // The Most Important Test In The Suite: A Client At The Expected Packet Rate Must Never Be Actioned, Because A False Positive Drops A Legitimate Player Mid-Match
    [Test]
    public async Task A_Source_At_The_Expected_Rate_Is_Never_Actioned()
    {
        using ViolationScoreContainer container = new ();

        bool everRefused = false;

        // Sixty Seconds Of Traffic At The Rate The Drain Is Sized For, Charged One Rate-Limit Weight Per Second
        for (int second = 0; second < 60; second++)
        {
            container.Charge(Source(), ViolationScoreContainer.RateLimitViolationWeight);

            if (container.IsWithinAllowance(Source()) is false)
                everRefused = true;

            container.Replenish();
        }

        await Assert.That(everRefused).IsFalse();
    }

    [Test]
    public async Task A_Source_Well_Above_The_Threshold_Is_Actioned()
    {
        using ViolationScoreContainer container = new ();

        bool refused = false;

        // Nothing Is Replenished, So The Allowance Is Exhausted
        for (int attempt = 0; attempt < 200; attempt++)
        {
            container.Charge(Source(), ViolationScoreContainer.RateLimitViolationWeight);

            if (container.IsWithinAllowance(Source()) is false)
            {
                refused = true;

                break;
            }
        }

        await Assert.That(refused).IsTrue();
    }

    [Test]
    public async Task An_Actioned_Source_Recovers_After_Draining()
    {
        using ViolationScoreContainer container = new ();

        while (container.IsWithinAllowance(Source()))
            container.Charge(Source(), ViolationScoreContainer.RateLimitViolationWeight);

        // A Minute Of Drain At The Configured Rate Is Far More Than The Threshold, So The Source Must Be Clear Again
        for (int second = 0; second < 60; second++)
            container.Replenish();

        await Assert.That(container.IsWithinAllowance(Source())).IsTrue();
    }

    [Test]
    public async Task Sources_Are_Scored_Independently()
    {
        using ViolationScoreContainer container = new ();

        while (container.IsWithinAllowance(Source(40000)))
            container.Charge(Source(40000), ViolationScoreContainer.RateLimitViolationWeight);

        await Assert.That(container.IsWithinAllowance(Source(40001))).IsTrue();
    }

    // A Weight Above The Threshold Would Throw Rather Than Refuse, So No Weight May Ever Exceed It
    [Test]
    public async Task Every_Weight_Is_Below_The_Actionable_Threshold()
    {
        int[] weights =
        [
            ViolationScoreContainer.TooShortViolationWeight,
            ViolationScoreContainer.UnauthenticatedViolationWeight,
            ViolationScoreContainer.RateLimitViolationWeight,
            ViolationScoreContainer.DuplicateViolationWeight
        ];

        using (Assert.Multiple())
        {
            foreach (int weight in weights)
            {
                await Assert.That(weight).IsGreaterThan(0);
                await Assert.That(weight).IsLessThan(ViolationScoreContainer.ActionableThreshold);
            }
        }
    }

    [Test]
    public async Task A_Single_Violation_Of_Any_Weight_Never_Actions_A_Source()
    {
        using ViolationScoreContainer container = new ();

        using (Assert.Multiple())
        {
            container.Charge(Source(41000), ViolationScoreContainer.TooShortViolationWeight);
            container.Charge(Source(41001), ViolationScoreContainer.UnauthenticatedViolationWeight);
            container.Charge(Source(41002), ViolationScoreContainer.RateLimitViolationWeight);
            container.Charge(Source(41003), ViolationScoreContainer.DuplicateViolationWeight);

            await Assert.That(container.IsWithinAllowance(Source(41000))).IsTrue();
            await Assert.That(container.IsWithinAllowance(Source(41001))).IsTrue();
            await Assert.That(container.IsWithinAllowance(Source(41002))).IsTrue();
            await Assert.That(container.IsWithinAllowance(Source(41003))).IsTrue();
        }
    }
}
```

No new package is required: replenishment is explicit, so no fake time source is needed.

- [ ] **Step 4: Run the tests to verify they fail**

Run: `dotnet build source/COMPEL.slnx`
Expected: FAIL — `ViolationScoreContainer` does not exist.

- [ ] **Step 5: Write the container**

```csharp
namespace COMPEL.Services.Proxy;

/// <summary>
///     Scores abusive behaviour per source address and reports when a source has exhausted its allowance.
///     Violations consume a source's allowance and the allowance refills when <see cref="Replenish"/> is called, which reproduces the reference proxy's accumulate-and-decay model: a source behaving normally never runs out, while one misbehaving does and recovers only once it stops.
///     No state is persisted and nothing is attributed to an account, because at this layer there is only an address and a datagram.
/// </summary>
internal sealed class ViolationScoreContainer : IDisposable
{
    // "BAN_THRESHOLD": The Score At Which A Source Is Acted Upon; Named For The Decision Rather Than Today's Action, Which Is A Drop
    internal const int ActionableThreshold = 4000;

    // "ESTIMATED_PACKETS_PER_SECOND": How Much Of A Source's Allowance Is Restored By Each Replenishment
    internal const int EstimatedPacketsPerSecond = 140;

    // "WARN_TOO_SHORT"
    internal const int TooShortViolationWeight = 200;

    // "WARN_UNAUTHENTICATED"
    internal const int UnauthenticatedViolationWeight = 200;

    // "WARN_LIMIT"
    internal const int RateLimitViolationWeight = 100;

    // "WARN_DUPE"
    internal const int DuplicateViolationWeight = 30;

    // One Limiter Per Source, Rather Than A "PartitionedRateLimiter", Because The Partitioned Wrapper Offers No Way To Drive Replenishment And Its Factory Has No Time Provider Overload
    private readonly ConcurrentDictionary<IPEndPoint, TokenBucketRateLimiter> limiters = new ();

    /// <summary>
    ///     Charges <paramref name="weight"/> against <paramref name="source"/>. The outcome is read separately through <see cref="IsWithinAllowance"/>, because a charge is recorded whether or not the source still has room.
    /// </summary>
    internal void Charge(IPEndPoint source, int weight)
    {
        using RateLimitLease lease = For(source).AttemptAcquire(weight);
    }

    /// <summary>
    ///     Whether <paramref name="source"/> is still within <see cref="ActionableThreshold"/>. Consumes nothing, so it is safe to call for every datagram.
    /// </summary>
    internal bool IsWithinAllowance(IPEndPoint source) => For(source).GetStatistics()?.CurrentAvailablePermits > 0;

    /// <summary>
    ///     Restores <see cref="EstimatedPacketsPerSecond"/> of allowance to every tracked source, and forgets any source whose allowance is fully restored so an idle address is not tracked indefinitely.
    ///     Called once per second by the proxy's maintenance loop, matching the reference proxy's housekeeping pass.
    /// </summary>
    internal void Replenish()
    {
        foreach (KeyValuePair<IPEndPoint, TokenBucketRateLimiter> entry in limiters)
        {
            entry.Value.TryReplenish();

            // A Source Whose Allowance Is Fully Restored Is Forgotten, So An Address That Has Stopped Misbehaving Is Not Tracked For The Life Of The Process
            // The Removed Limiter Is Deliberately Not Disposed Here: A Datagram Thread May Already Hold The Same Reference, And Disposing It Underneath That Thread Would Throw On The Hot Path
            // Dropping The Reference Leaks Nothing, Because Automatic Replenishment Is Off And The Limiter Owns No Timer; At Worst One Charge Lands On The Orphaned Instance, Which Was At Full Allowance Anyway
            if (entry.Value.GetStatistics()?.CurrentAvailablePermits >= ActionableThreshold)
                limiters.TryRemove(entry.Key, out _);
        }
    }

    private TokenBucketRateLimiter For(IPEndPoint source) => limiters.GetOrAdd(source, _ => new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
    {
        TokenLimit = ActionableThreshold,
        TokensPerPeriod = EstimatedPacketsPerSecond,

        // Replenishment Is Driven By The Maintenance Loop So The Drain Is Deterministic And Testable Without Waiting On Wall-Clock Time
        AutoReplenishment = false,
        ReplenishmentPeriod = TimeSpan.FromSeconds(1),

        // The Datagram Path Must Never Wait, So Nothing Is Queued
        QueueLimit = 0,
        QueueProcessingOrder = QueueProcessingOrder.OldestFirst
    }));

    public void Dispose()
    {
        foreach (TokenBucketRateLimiter limiter in limiters.Values)
            limiter.Dispose();

        limiters.Clear();
    }
}
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet build source/COMPEL.slnx && dotnet test source/COMPEL.slnx`
Expected: build succeeds with 0 warnings; all tests pass, the rate test included.

- [ ] **Step 7: Commit**

```bash
git add source/COMPEL/Services/Proxy/ViolationScoreContainer.cs source/COMPEL/Internals/UsingDirectives.cs source/COMPEL.Tests/Services/Proxy/ViolationScoreContainerTests.cs source/COMPEL.Tests/COMPEL.Tests.csproj
git commit -m "Score Proxy Abuse Per Source With A Draining Allowance"
```

---

### Task 4: Per-Challenge Session State

**Files:**
- Create: `source/COMPEL/Services/Proxy/ChallengeWindow.cs`
- Create: `source/COMPEL/Services/Proxy/SessionChallengeState.cs`
- Test: `source/COMPEL.Tests/Services/Proxy/ChallengeWindowTests.cs`
- Test: `source/COMPEL.Tests/Services/Proxy/SessionChallengeStateTests.cs`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces: `ChallengeWindow` with `ChallengeWindow(uint challenge, ushort quota)`, `uint Challenge`, and `bool TryAdmit(ushort counter, out ChallengeAdmission admission)`; `enum ChallengeAdmission { Admitted, OverQuota, Duplicate }`; and `SessionChallengeState` with `void Rotate(uint challenge, ushort quota)` and `ChallengeWindow? Match(uint challenge)`.

`SessionChallengeState` is a separate internal type rather than private state inside `UDPForwarder` **specifically so the renewal grace can be tested**. The spec identifies that behaviour as the most important test in the set, and it is untestable while it lives inside a private nested class.

- [ ] **Step 1: Fact verification**

```bash
cd "source/COMPEL/bin/Publish/HoN_Proxy/HoN/branches/retail/Tool/HoNProxy"
sed -n '672,680p' main.cpp
sed -n '699,712p' main.cpp
```

Expected: the quota comparison `*ctr >= (game ? CHALLENGE_MAX_CTR : CHALLENGE_MAX_CTR_VOICE)` adding `WARN_LIMIT`; and the duplicate check indexing a per-challenge structure by `*ctr`. Confirm the quota comparison precedes the duplicate index, because the counter is client-controlled and the duplicate store is sized to the quota.

- [ ] **Step 2: Write the failing tests**

```csharp
namespace COMPEL.Tests.Services.Proxy;

/// <summary>
///     Verifies one challenge's packet allowance: that a counter within the quota is admitted once, that the quota is enforced, and that a repeated counter is rejected as a duplicate.
/// </summary>
public sealed class ChallengeWindowTests
{
    [Test]
    public async Task A_Counter_Within_The_Quota_Is_Admitted()
    {
        ChallengeWindow window = new (challenge: 42, quota: 10);

        bool admitted = window.TryAdmit(0, out ChallengeAdmission admission);

        using (Assert.Multiple())
        {
            await Assert.That(admitted).IsTrue();
            await Assert.That(admission).IsEqualTo(ChallengeAdmission.Admitted);
        }
    }

    [Test]
    public async Task A_Counter_At_Or_Above_The_Quota_Is_Over_Quota()
    {
        ChallengeWindow window = new (challenge: 42, quota: 10);

        using (Assert.Multiple())
        {
            await Assert.That(window.TryAdmit(10, out ChallengeAdmission atQuota)).IsFalse();
            await Assert.That(atQuota).IsEqualTo(ChallengeAdmission.OverQuota);

            await Assert.That(window.TryAdmit(9999, out ChallengeAdmission farAbove)).IsFalse();
            await Assert.That(farAbove).IsEqualTo(ChallengeAdmission.OverQuota);
        }
    }

    // A Counter Beyond The Quota Must Be Refused Before It Is Used To Index The Duplicate Store, Which Is Sized To The Quota
    [Test]
    public async Task The_Maximum_Possible_Counter_Does_Not_Index_Out_Of_Range()
    {
        ChallengeWindow window = new (challenge: 42, quota: 10);

        bool admitted = window.TryAdmit(ushort.MaxValue, out ChallengeAdmission admission);

        using (Assert.Multiple())
        {
            await Assert.That(admitted).IsFalse();
            await Assert.That(admission).IsEqualTo(ChallengeAdmission.OverQuota);
        }
    }

    [Test]
    public async Task A_Repeated_Counter_Is_A_Duplicate()
    {
        ChallengeWindow window = new (challenge: 42, quota: 10);

        window.TryAdmit(3, out _);

        bool admitted = window.TryAdmit(3, out ChallengeAdmission admission);

        using (Assert.Multiple())
        {
            await Assert.That(admitted).IsFalse();
            await Assert.That(admission).IsEqualTo(ChallengeAdmission.Duplicate);
        }
    }

    [Test]
    public async Task Every_Counter_Below_The_Quota_Is_Admitted_Exactly_Once()
    {
        const ushort quota = 64;

        ChallengeWindow window = new (challenge: 1, quota: quota);

        using (Assert.Multiple())
        {
            for (ushort counter = 0; counter < quota; counter++)
                await Assert.That(window.TryAdmit(counter, out _)).IsTrue();

            for (ushort counter = 0; counter < quota; counter++)
                await Assert.That(window.TryAdmit(counter, out _)).IsFalse();
        }
    }

    // The Same Counter Under A New Challenge Is Legitimate, Because The Client Restarts Its Counter When It Accepts One
    [Test]
    public async Task A_New_Window_Admits_A_Counter_The_Previous_One_Consumed()
    {
        ChallengeWindow first = new (challenge: 1, quota: 10);
        first.TryAdmit(5, out _);

        ChallengeWindow second = new (challenge: 2, quota: 10);

        await Assert.That(second.TryAdmit(5, out _)).IsTrue();
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet build source/COMPEL.slnx`
Expected: FAIL — `ChallengeWindow` and `ChallengeAdmission` do not exist.

- [ ] **Step 4: Write the challenge window**

```csharp
namespace COMPEL.Services.Proxy;

/// <summary>
///     Why a datagram was or was not admitted under a challenge.
/// </summary>
internal enum ChallengeAdmission
{
    Admitted,
    OverQuota,
    Duplicate
}

/// <summary>
///     One challenge's packet allowance for one client.
///     The client stamps an increasing counter into each datagram and restarts it when it accepts a new challenge, so each window admits every counter below its quota exactly once.
///     The quota is checked before the counter is used to index the seen set, because the counter arrives from the client and may be any value the field can hold.
/// </summary>
internal sealed class ChallengeWindow
{
    private readonly bool[] seen;

    public ChallengeWindow(uint challenge, ushort quota)
    {
        Challenge = challenge;
        Quota = quota;
        seen = new bool[quota];
    }

    public uint Challenge { get; }

    public ushort Quota { get; }

    /// <summary>
    ///     Admits <paramref name="counter"/> if it is within the quota and has not been seen under this challenge before.
    /// </summary>
    internal bool TryAdmit(ushort counter, out ChallengeAdmission admission)
    {
        if (counter >= Quota)
        {
            admission = ChallengeAdmission.OverQuota;

            return false;
        }

        lock (seen)
        {
            if (seen[counter])
            {
                admission = ChallengeAdmission.Duplicate;

                return false;
            }

            seen[counter] = true;
        }

        admission = ChallengeAdmission.Admitted;

        return true;
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet build source/COMPEL.slnx && dotnet test source/COMPEL.slnx`
Expected: build succeeds with 0 warnings; all tests pass.

- [ ] **Step 6: Write the failing renewal grace tests**


```csharp
namespace COMPEL.Tests.Services.Proxy;

/// <summary>
///     Verifies that a client is still admitted immediately after its challenge is renewed, using the challenge it echoed rather than only the newest one.
/// </summary>
public sealed class SessionChallengeStateTests
{
    // Without This, Every Renewal Would Drop The Datagrams Already In Flight: A False Positive For Every Player On Every Rotation
    [Test]
    public async Task A_Datagram_Echoing_The_Previous_Challenge_Is_Still_Matched()
    {
        SessionChallengeState state = new ();

        state.Rotate(challenge: 100, quota: 64);
        state.Rotate(challenge: 200, quota: 64);

        using (Assert.Multiple())
        {
            await Assert.That(state.Match(200)).IsNotNull();
            await Assert.That(state.Match(100)).IsNotNull();
        }
    }

    // The Previous Window Keeps Its Own Consumed Counters, So A Client Mid-Rotation Is Judged Against The Window It Was Actually Using
    [Test]
    public async Task The_Previous_Window_Retains_Its_Own_Consumed_Counters()
    {
        SessionChallengeState state = new ();

        state.Rotate(challenge: 100, quota: 64);

        ChallengeWindow? first = state.Match(100);
        first?.TryAdmit(7, out _);

        state.Rotate(challenge: 200, quota: 64);

        using (Assert.Multiple())
        {
            await Assert.That(state.Match(100)?.TryAdmit(7, out _)).IsFalse();
            await Assert.That(state.Match(200)?.TryAdmit(7, out _)).IsTrue();
        }
    }

    [Test]
    public async Task A_Challenge_Older_Than_The_Previous_One_Is_Not_Matched()
    {
        SessionChallengeState state = new ();

        state.Rotate(challenge: 100, quota: 64);
        state.Rotate(challenge: 200, quota: 64);
        state.Rotate(challenge: 300, quota: 64);

        await Assert.That(state.Match(100)).IsNull();
    }

    [Test]
    public async Task An_Unknown_Challenge_Is_Not_Matched()
    {
        SessionChallengeState state = new ();

        state.Rotate(challenge: 100, quota: 64);

        using (Assert.Multiple())
        {
            await Assert.That(state.Match(999)).IsNull();
            await Assert.That(state.Match(0)).IsNull();
        }
    }

    [Test]
    public async Task No_Challenge_Is_Matched_Before_The_First_Rotation()
    {
        SessionChallengeState state = new ();

        await Assert.That(state.Match(100)).IsNull();
    }
}
```

- [ ] **Step 7: Run the tests to verify they fail**

Run: `dotnet build source/COMPEL.slnx`
Expected: FAIL - `SessionChallengeState` does not exist.

- [ ] **Step 8: Write the session challenge state**

```csharp
namespace COMPEL.Services.Proxy;

/// <summary>
///     The challenges one client session may currently use: the challenge most recently issued to it, and the one that replaced.
///     Both are retained because a challenge is renewed while the client is sending, so datagrams already in flight carry the previous challenge and a counter that has not restarted.
///     Matching only the newest challenge would drop those datagrams on every renewal.
/// </summary>
internal sealed class SessionChallengeState
{
    private readonly Lock stateLock = new ();

    private ChallengeWindow? current;
    private ChallengeWindow? previous;

    /// <summary>
    ///     Records a newly issued challenge, retaining the one it replaces.
    /// </summary>
    internal void Rotate(uint challenge, ushort quota)
    {
        lock (stateLock)
        {
            previous = current;
            current = new ChallengeWindow(challenge, quota);
        }
    }

    /// <summary>
    ///     The window for the challenge the client echoed, or <see langword="null"/> when it echoed neither of the two the session holds.
    /// </summary>
    internal ChallengeWindow? Match(uint challenge)
    {
        lock (stateLock)
        {
            if (current is not null && current.Challenge == challenge)
                return current;

            return previous is not null && previous.Challenge == challenge ? previous : null;
        }
    }
}
```

- [ ] **Step 9: Run the tests to verify they pass**

Run: `dotnet build source/COMPEL.slnx && dotnet test source/COMPEL.slnx`
Expected: build succeeds with 0 warnings; all tests pass.

- [ ] **Step 10: Commit**

```bash
git add source/COMPEL/Services/Proxy/ChallengeWindow.cs source/COMPEL/Services/Proxy/SessionChallengeState.cs source/COMPEL.Tests/Services/Proxy/ChallengeWindowTests.cs source/COMPEL.Tests/Services/Proxy/SessionChallengeStateTests.cs
git commit -m "Admit Each Challenge Counter Once, Tolerating A Renewal"
```

---

### Task 5: Wire The Pipeline Into The Forwarder

**Files:**
- Modify: `source/COMPEL/Services/Proxy/UDPForwarder.cs`
- Modify: `source/COMPEL/Services/Proxy/UDPProxyService.cs:59-61` and its `TryAddForwarder` method
- Test: `source/COMPEL.Tests/Services/Proxy/UDPForwarderTests.cs`

**Interfaces:**
- Consumes: `ProxyForwarderKind`, `ChallengeQuota`, `ClientPacketReader`, `ViolationScoreContainer`, `ChallengeWindow`, `ChallengeAdmission`.
- Produces: `UDPForwarder(int publicPort, int localPort, ProxyForwarderKind kind, TimeSpan challengeRenewalInterval, ViolationScoreContainer scoreContainer, ILogger logger)`; `int UDPForwarder.DroppedDatagramCount`.

- [ ] **Step 1: Fact verification**

Re-read the current forwarder and confirm the shape the changes assume:

```bash
grep -n "ChallengeMaximumCounter\|ChallengeMaximumGameCommandCounter\|SendChallenge\|GetOrCreateSession\|class ClientSession" source/COMPEL/Services/Proxy/UDPForwarder.cs
grep -n "TryAddForwarder\|ChallengeRenewalInterval" source/COMPEL/Services/Proxy/UDPProxyService.cs
```

Expected: `SendChallenge` currently generates a challenge value and records it nowhere; `ClientSession` holds no challenge state; `TryAddForwarder` passes a `string` kind used only for logging. All three change in this task.

- [ ] **Step 2: Write the failing tests**

The existing `UDPForwarderTests` already relays over loopback; add to it.

```csharp
    // A Datagram Below The Minimum Length Must Never Reach The Server, Because The Reader Refuses To Read Its Fields
    [Test]
    public async Task A_Short_Datagram_Is_Dropped_Rather_Than_Relayed()
    {
        using ViolationScoreContainer container = new ();
        using Socket server = new (AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        server.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        int localPort = ((IPEndPoint)server.LocalEndPoint!).Port;

        using UDPForwarder forwarder = new (0, localPort, ProxyForwarderKind.Game, TimeSpan.FromSeconds(10), container, NullLogger.Instance);

        using CancellationTokenSource cancellation = new (TimeSpan.FromSeconds(5));

        _ = forwarder.Run(cancellation.Token);

        using Socket client = new (AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        client.SendTo(new byte[8], new IPEndPoint(IPAddress.Loopback, forwarder.PublicPort));

        await Task.Delay(TimeSpan.FromMilliseconds(500), cancellation.Token);

        using (Assert.Multiple())
        {
            await Assert.That(server.Available).IsEqualTo(0);
            await Assert.That(forwarder.DroppedDatagramCount).IsGreaterThan(0);
        }
    }
```

Note: the existing tests construct `UDPForwarder` with the old three-argument constructor and must be updated to the new signature in Step 4, passing `ProxyForwarderKind.Game`, `TimeSpan.FromSeconds(10)` and a container.

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet build source/COMPEL.slnx`
Expected: FAIL — the constructor takes three arguments and `DroppedDatagramCount` does not exist.

- [ ] **Step 4: Change the forwarder**

Replace the three quota constants with values derived per kind, and store the kind, interval and container:

```csharp
    private readonly ProxyForwarderKind kind;
    private readonly TimeSpan challengeRenewalInterval;
    private readonly ViolationScoreContainer scoreContainer;
    private readonly ushort packetQuota;
    private readonly ushort gameCommandQuota;

    private int droppedDatagramCount;

    public int DroppedDatagramCount => Volatile.Read(ref droppedDatagramCount);
```

Set them in the constructor, after the existing assignments:

```csharp
        this.kind = kind;
        this.challengeRenewalInterval = challengeRenewalInterval;
        this.scoreContainer = scoreContainer;

        packetQuota = ChallengeQuota.ForKind(kind, challengeRenewalInterval);
        gameCommandQuota = ChallengeQuota.GameCommandForInterval(challengeRenewalInterval);
```

Advertise them instead of the maxima, replacing the two `ChallengeMaximumCounter` and `ChallengeMaximumGameCommandCounter` writes in `BuildChallengePacket`, which becomes an instance method:

```csharp
        BinaryPrimitives.WriteUInt16LittleEndian(payload[10..], packetQuota);
        BinaryPrimitives.WriteUInt16LittleEndian(payload[12..], gameCommandQuota);
```

Give `ClientSession` its challenge state, which delegates to the type from Task 4:

```csharp
        // Rotation And Matching Live In "SessionChallengeState" So The Renewal Grace Is Testable Outside This Private Class
        public SessionChallengeState Challenges { get; } = new ();
```

Have `SendChallenge` record what it issued. Change its signature to take the session and call `session.Challenges.Rotate(sequence, packetQuota)` immediately before sending, and update both call sites (`Run` for a newly created session, and `ChallengeActiveSessions`, which must look the session up rather than iterating keys alone).

Then replace the forwarding block in `Run`, immediately after `session.Touch()`, with the ordered pipeline:

```csharp
            ReadOnlySpan<byte> datagram = buffer.AsSpan(0, result.ReceivedBytes);

            // The Length Guard Runs First So No Field Is Ever Read Out Of Range
            if (ClientPacketReader.TryRead(datagram, kind, out uint challenge, out ushort counter) is false)
            {
                Drop(client, ViolationScoreContainer.TooShortViolationWeight, "Too Short");

                continue;
            }

            ChallengeWindow? window = session.Challenges.Match(challenge);

            if (window is null)
            {
                // The Client Has Not Accepted A Challenge Yet, So It Is Held To The Unauthenticated Total Rather Than A Rate
                if (counter >= ChallengeQuota.UnauthenticatedPacketQuota)
                {
                    Drop(client, ViolationScoreContainer.UnauthenticatedViolationWeight, "Unauthenticated");

                    continue;
                }
            }

            // The Quota Is Checked Before The Counter Indexes The Seen Set, Because The Counter Arrives From The Client
            else if (window.TryAdmit(counter, out ChallengeAdmission admission) is false)
            {
                int weight = admission is ChallengeAdmission.Duplicate
                    ? ViolationScoreContainer.DuplicateViolationWeight
                    : ViolationScoreContainer.RateLimitViolationWeight;

                Drop(client, weight, admission.ToString());

                continue;
            }

            if (scoreContainer.IsWithinAllowance(client) is false)
            {
                Drop(client, 0, "Over Threshold");

                continue;
            }

            try { await session.UpstreamSocket.SendAsync(buffer.AsMemory(0, result.ReceivedBytes), SocketFlags.None, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (Exception exception) { logger.LogDebug(exception, "Failed To Forward Datagram To Server For {Client}", client); }
```

Add the drop helper, which charges the score and logs only the first drop per source so a flood cannot become a log flood:

```csharp
    private void Drop(IPEndPoint client, int weight, string reason)
    {
        Interlocked.Increment(ref droppedDatagramCount);

        if (weight > 0)
            scoreContainer.Charge(client, weight);

        if (reportedDrops.TryAdd(client, true))
            logger.LogWarning("Dropped A Datagram From {Client} On Public Port {Port} ({Reason}); Further Drops From This Source Are Not Logged", client, PublicPort, reason);
    }
```

with `private readonly ConcurrentDictionary<IPEndPoint, bool> reportedDrops = new ();` beside the other fields.

- [ ] **Step 5: Change the proxy service to pass the kind and the container**

In `UDPProxyService`, add a `private readonly ViolationScoreContainer scoreContainer = new ();` field, dispose it alongside the forwarders, and call `scoreContainer.Replenish();` once per iteration of `RunMaintenanceLoop` so a source's allowance drains on the one-second cadence the container expects. Then change the two calls to pass the enum, and change `TryAddForwarder`:

```csharp
            TryAddForwarder(ports.PublicGameStart + instance, ports.LocalGameStart + instance, ProxyForwarderKind.Game);
            TryAddForwarder(ports.PublicVoiceStart + instance, ports.LocalVoiceStart + instance, ProxyForwarderKind.Voice);
```

```csharp
    private void TryAddForwarder(int publicPort, int localPort, ProxyForwarderKind kind)
    {
        try
        {
            forwarders.Add(new UDPForwarder(publicPort, localPort, kind, ChallengeRenewalInterval, scoreContainer, logger));
        }

        catch (Exception exception)
        {
            Interlocked.Increment(ref failedForwarderCount);

            logger.LogError(exception, "Failed To Bind The {Kind} Proxy On Public Port {PublicPort}", kind, publicPort);
        }
    }
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet build source/COMPEL.slnx && dotnet test source/COMPEL.slnx`
Expected: build succeeds with 0 warnings; all tests pass, including the existing relay tests with their updated constructor calls.

- [ ] **Step 7: Commit**

```bash
git add source/COMPEL/Services/Proxy/UDPForwarder.cs source/COMPEL/Services/Proxy/UDPProxyService.cs source/COMPEL.Tests/Services/Proxy/UDPForwarderTests.cs
git commit -m "Validate And Rate Limit Client Datagrams In The Proxy"
```

---

### Task 6: Report Through The Control Plane, Then Verify Live

**Files:**
- Modify: `source/COMPEL/Services/Proxy/UDPProxyService.cs`
- Modify: `source/COMPEL/Endpoints/Contracts.cs`
- Modify: `source/COMPEL/Endpoints/ControlPlaneEndpoints.cs`
- Modify: `source/COMPEL/Services/Proxy/UDPProxyService.cs` (the proxy TODO)

**Interfaces:**
- Consumes: `UDPForwarder.DroppedDatagramCount`.
- Produces: `int UDPProxyService.DroppedDatagramCount`; `bool UDPProxyService.IsUnderAttack`; `ProxyDroppedDatagramCount` and `ProxyIsUnderAttack` members on `StatusResponse`.

- [ ] **Step 1: Fact verification**

```bash
grep -n "ProxyFailedForwarderCount" source/COMPEL/Endpoints/Contracts.cs source/COMPEL/Endpoints/ControlPlaneEndpoints.cs
grep -n "TODO" source/COMPEL/Services/Proxy/UDPProxyService.cs
```

Expected: the existing failed-forwarder member shows the pattern the new member follows; and the existing TODO is the one to extend with the deferred watermark work.

- [ ] **Step 2: Expose the aggregate count**

In `UDPProxyService`:

```csharp
    /// <summary>
    ///     The number of client datagrams the proxy has refused to relay, across every forwarder.
    /// </summary>
    public int DroppedDatagramCount => forwarders.Sum(forwarder => forwarder.DroppedDatagramCount);
```

- [ ] **Step 3: Add it to the status contract**

In `Contracts.cs`, after `ProxyFailedForwarderCount`:

```csharp
    int ProxyDroppedDatagramCount,
    bool ProxyIsUnderAttack,
```

and in `ControlPlaneEndpoints.cs`, after the corresponding line:

```csharp
                ProxyDroppedDatagramCount: proxy.DroppedDatagramCount,
                ProxyIsUnderAttack: proxy.IsUnderAttack,
```

- [ ] **Step 4: Add the under-attack indicator**

The reference keeps a global counter incremented per violation and reset periodically, so a broad attack is distinguishable from one misbehaving client. Confirm the constant first:

```bash
grep -nE "define UNDER_ATTACK_THRESHOLD" "source/COMPEL/bin/Publish/HoN_Proxy/HoN/branches/retail/Tool/HoNProxy/main.cpp"
```

Expected: `UNDER_ATTACK_THRESHOLD 1000`.

In `UDPProxyService`, beside the existing fields:

```csharp
    // "UNDER_ATTACK_THRESHOLD": Drops Within One Reset Window Above Which The Host Is Treated As Under Attack Rather Than Merely Refusing One Misbehaving Client
    private const int UnderAttackThreshold = 1000;

    private int dropsSinceReset;
    private bool isUnderAttack;

    /// <summary>
    ///     Whether the proxy refused more datagrams in the last reset window than a single misbehaving client could account for.
    /// </summary>
    public bool IsUnderAttack => Volatile.Read(ref isUnderAttack);
```

In `RunMaintenanceLoop`, alongside the `scoreContainer.Replenish();` call added in Task 5, sample the aggregate drop count and reset the window every five minutes, matching the reference's cadence:

```csharp
            int drops = DroppedDatagramCount;
            int dropsThisWindow = drops - Interlocked.Exchange(ref dropsSinceReset, drops);

            if (dropsThisWindow > UnderAttackThreshold)
            {
                Volatile.Write(ref isUnderAttack, true);

                logger.LogWarning("The Proxy Refused {Drops} Datagram(s) Recently, Which Exceeds The Under-Attack Threshold Of {Threshold}", dropsThisWindow, UnderAttackThreshold);
            }

            else
            {
                Volatile.Write(ref isUnderAttack, false);
            }
```

Sampling the aggregate rather than incrementing per drop keeps the datagram path free of another shared counter.
- [ ] **Step 5: Record the deferred work**

Extend the existing TODO above `UDPProxyService` so the deferred checks are documented where the next reader will look:

```csharp
// TODO: The Proxy Validates Datagram Length, The Per-Challenge Packet Quota, And Duplicate Counters, And Scores Abuse Per Source; It Does Not Yet Validate The Watermarks
// TODO: The Reference Proxy Also Checks A Constant Per-Region Watermark And A Dynamic CRC32C One, Which Together Are Its Anti-Cheat Signal; Adding Them Needs A Region Setting COMPEL Has No Equivalent For, And Carries A Higher False-Positive Cost Than The Checks Above
```

- [ ] **Step 6: Build and test**

Run: `dotnet build source/COMPEL.slnx && dotnet test source/COMPEL.slnx`
Expected: build succeeds with 0 warnings; all tests pass.

- [ ] **Step 7: Verify Native AOT**

The rate limiting library is reflection-free, but trim and AOT analysers only run at publish time, so this is the first opportunity to confirm it.

Run: `pwsh -NoProfile -File scripts/Publish-Native-AOT-Release.ps1`
Expected: the publish succeeds with no `IL2xxx`, `IL3xxx` or trim warnings. If any appear, stop and report rather than suppressing them.

- [ ] **Step 8: Verify against a real match**

With the local NEXUS stack running, `Gateway` set to `localhost`, `UseProxy` set to `true`, `RuntimeArtefactsPath` set to a clean directory, and COMPEL started from a directory whose path contains a whitespace character, play a match through the proxy.

Confirm all of the following:

- the match starts and the server reports `Disconnects(0)`;
- `/status` reports `proxyDroppedDatagramCount` of **zero**, because a legitimate client must never be dropped;
- no `Dropped A Datagram` warning appears in `COMPEL.log`;
- the match server logs the client's source as the proxy's upstream socket, confirming traffic still relays through the proxy.

A non-zero drop count against a legitimate client is a failure, not a curiosity: stop and diagnose before shipping.

- [ ] **Step 9: Commit**

```bash
git add source/COMPEL/Services/Proxy/UDPProxyService.cs source/COMPEL/Endpoints/Contracts.cs source/COMPEL/Endpoints/ControlPlaneEndpoints.cs
git commit -m "Report Proxy Datagram Drops Through The Control Plane"
```

---

## Final Verification

- [ ] `dotnet build source/COMPEL.slnx` succeeds with 0 warnings.
- [ ] `dotnet test source/COMPEL.slnx` passes, with at least 29 new tests across the six new units.
- [ ] `scripts/Publish-Native-AOT-Release.ps1` succeeds with no trim or AOT warnings.
- [ ] A real match through the proxy shows `Disconnects(0)`, a drop count of zero, and `proxyIsUnderAttack` false.
- [ ] No file uses `var`, an abbreviation, American spelling, or the null-forgiving operator.
- [ ] Every new constant carries the reference `#define` name in a trailing comment.
