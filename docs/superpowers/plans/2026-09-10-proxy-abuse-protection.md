# Proxy Abuse Protection Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Validate, rate limit and score incoming client datagrams in COMPEL's managed proxy, so a client cannot flood or malform its way through to a match server.

**Architecture:** Each datagram passes an ordered pipeline before it is relayed: a length guard, then a read of the client-supplied challenge and counter, then per-challenge quota and duplicate checks, then a per-source decaying violation score. The score container owns one framework token limiter per source, replenished explicitly by the proxy's existing maintenance loop; everything else is a small pure unit with its own tests. Nothing in the pipeline awaits.

**Tech Stack:** .NET 11, ASP.NET Core with Native AOT, TUnit on the Microsoft Testing Platform. No new package is referenced: `ConcurrentDictionary` and `TimeProvider` are both core.

**Spec:** `docs/superpowers/specs/2026-09-10-proxy-abuse-protection-design.md`

## Global Constraints

- **Every task starts with its Fact Verification step.** Do not write code for a task until its cited reference lines have been re-read and confirmed. If a citation does not say what the plan claims, stop and report rather than proceeding.
- Reference implementation: `source/COMPEL/bin/Publish/HoN_Proxy/HoN/branches/retail/Tool/HoNProxy/main.cpp`.
- **Ordering is a safety property:** the quota check must precede the duplicate check. The counter is client-controlled up to 65535 while the duplicate bitmap is sized to the quota, so the reverse order indexes out of bounds.
- **No violation weight may exceed `ActionableThreshold`.** A weight at or above the threshold would action a source on a single anomaly, which is what the weighting exists to prevent.
- **The score accumulates and saturates; it never refuses a charge.** `ActionableThreshold` (4000) is where a source starts being acted upon and `MaximumViolationScore` (20000) is where the score stops climbing. Collapsing the two loses the reference's graduated persistence.
- The datagram path must never await, and must not allocate per datagram: pass state to the `ConcurrentDictionary` factory overloads rather than capturing it in a lambda.
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
- Create: `source/COMPEL.Tests/Services/Proxy/ControllableTimeProvider.cs`
- Test: `source/COMPEL.Tests/Services/Proxy/ViolationScoreContainerTests.cs`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces: `ViolationScoreContainer(TimeProvider timeProvider)` with `void ChargeArrival(IPEndPoint source)`, `void ChargeViolation(IPEndPoint source, int weight)`, `bool IsWithinAllowance(IPEndPoint source)`, `int Score(IPEndPoint source)`, `int TrackedSourceCount`, `void Drain()`, and the constants `ActionableThreshold`, `MaximumViolationScore`, `EstimatedPacketsPerSecond`, `PacketScore`, `TooShortViolationWeight`, `UnauthenticatedViolationWeight`, `RateLimitViolationWeight`, `DuplicateViolationWeight`, `ActionedViolationWeight`.
- The container is **not** `IDisposable`: it holds no unmanaged resource. Do not wrap it in `using`.

**Why this is not built on `System.Threading.RateLimiting`.** A token container looks like the dual of accumulate-and-decay, and it was the first choice, but three measured properties of `TokenBucketRateLimiter` make it unable to express this model. All three were verified against `System.Threading.RateLimiting` 11.0.0.0 as loaded from `Microsoft.AspNetCore.App/11.0.0-preview.7`, which is what a `net11.0` target resolves:

- `AttemptAcquire` is all-or-nothing. A charge larger than the remaining permits fails and consumes nothing, where the reference always accumulates. Measured on a complete token-bucket implementation of this very component: after 400 duplicate-weight charges — a reference score of 12000, three times the threshold — the source was still within its allowance, parked indefinitely at 10 permits. Any weight that does not divide the threshold exactly lets a source sit just below it and never be actioned.
- The counter cannot exceed `TokenLimit`, so `MaximumViolationScore` is unrepresentable and the above-threshold escalation has nowhere to go.
- `TryReplenish` scales what it restores by real elapsed time (with a 1 ms period and 140 tokens per period, one call after 1000 ms restored the full 4000), `ReplenishmentPeriod` is rejected at `TimeSpan.Zero`, a one-tick period restores the whole limit even in a tight loop, and no `TimeProvider` exists on `TokenBucketRateLimiterOptions`.

What remains is a weighted score with linear decay, which the framework has no primitive for. It is implemented directly over a `ConcurrentDictionary<IPEndPoint, int>` with an injected `TimeProvider`, which matches the reference exactly and is deterministic under test.

**Two operations, not one.** The reference charges a source twice per datagram in the general case, and the two are kept separate here so neither is applied twice:

- `ChargeArrival` is the unconditional per-packet cost (`main.cpp:534`, `++(*warn)`), plus `ActionedViolationWeight` when the source is already over the threshold (`main.cpp:542-543`). Called exactly once per datagram.
- `ChargeViolation` adds a specific violation's weight. Called at most once per datagram, only when a check fails.

**The per-packet cost is what gives the drain rate its meaning.** `ESTIMATED_PACKETS_PER_SECOND` is documented in the reference as the rate a client is expected to stay under, and it is both the per-second drain and the break-even arrival rate: a source at or below 140 packets a second nets zero, one above it accumulates with no violation at all. A design that scored violations alone would leave a well-behaved source permanently at zero and make the drain rate arbitrary.

- [ ] **Step 1: Fact verification**

```bash
cd "source/COMPEL/bin/Publish/HoN_Proxy/HoN/branches/retail/Tool/HoNProxy"
grep -nE "define (BAN_THRESHOLD|MAX_WARN_COUNT|ESTIMATED_PACKETS_PER_SECOND|WARN_TOO_SHORT|WARN_UNAUTHENTICATED|WARN_LIMIT|WARN_DUPE|WARN_BANNED)" main.cpp
sed -n '532,545p' main.cpp
sed -n '1175,1193p' main.cpp
```

Expected, and every one of these is load-bearing for the code below:

- threshold 4000; maximum 20000; drain 140 per second; weights 200 (too short), 200 (unauthenticated), 100 (rate limit), 30 (duplicate), 10 (banned).
- `verify` opens with `++(*warn);`, unconditionally, before any check — the per-packet cost.
- immediately after it, `if (*warn > BAN_THRESHOLD)` and, nested inside, `if (*warn < MAX_WARN_COUNT) { (*warn) += WARN_BANNED; }` — the above-threshold escalation, with the reference's own comment explaining it is deliberate.
- the housekeeping loop gated on `elapsed_milliseconds > 900`, subtracting `elapsed_milliseconds * ESTIMATED_PACKETS_PER_SECOND / 1000.0f` and flooring at zero — a drain proportional to elapsed time, not a fixed amount.

Confirm every violation weight is below the threshold: a weight at or above it would action a source on a single anomaly, which is what the weighting exists to prevent.

- [ ] **Step 2: Write the controllable time provider**

The drain is proportional to elapsed time, so the tests move the clock rather than waiting on it. `TimeProvider.GetElapsedTime` is built on `GetTimestamp` and `TimestampFrequency`, so overriding those two is enough and no test package is needed.

```csharp
namespace COMPEL.Tests.Services.Proxy;

/// <summary>
///     A time provider whose clock only moves when a test moves it, so behaviour proportional to elapsed time can be asserted exactly rather than waited for.
/// </summary>
internal sealed class ControllableTimeProvider : TimeProvider
{
    private long timestamp;

    /// <summary>
    ///     Timestamps are counted in ticks, so an advance of a given interval moves the clock by exactly that interval.
    /// </summary>
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => timestamp;

    internal void Advance(TimeSpan interval) => timestamp += interval.Ticks;
}
```

- [ ] **Step 3: Write the failing tests**

```csharp
namespace COMPEL.Tests.Services.Proxy;

/// <summary>
///     Verifies the per-source violation score: that a source within the expected packet rate is never actioned, that one above it or violating the checks is, that the score saturates and drains as the reference's does, and that a source which keeps pushing while actioned stays actioned for longer.
/// </summary>
public sealed class ViolationScoreContainerTests
{
    private static readonly TimeSpan OneSecond = TimeSpan.FromSeconds(1);

    private static IPEndPoint Source(int port = 40000) => new (IPAddress.Parse("203.0.113.5"), port);

    // The Most Important Test In The Suite: A Client At The Expected Packet Rate Must Never Be Actioned, Because A False Positive Drops A Legitimate Player Mid-Match
    [Test]
    public async Task A_Source_At_The_Expected_Packet_Rate_Is_Never_Actioned()
    {
        ControllableTimeProvider clock = new ();
        ViolationScoreContainer container = new (clock);

        bool everActioned = false;

        // Sixty Seconds Of Traffic At Exactly The Rate The Drain Is Sized For
        for (int second = 0; second < 60; second++)
        {
            for (int packet = 0; packet < ViolationScoreContainer.EstimatedPacketsPerSecond; packet++)
            {
                container.ChargeArrival(Source());

                if (container.IsWithinAllowance(Source()) is false)
                    everActioned = true;
            }

            clock.Advance(OneSecond);
            container.Drain();
        }

        await Assert.That(everActioned).IsFalse();
    }

    // The Per-Packet Cost Exists So That A Flood Carrying No Detectable Violation Is Still Scored
    [Test]
    public async Task A_Source_Above_The_Expected_Packet_Rate_Is_Eventually_Actioned()
    {
        ControllableTimeProvider clock = new ();
        ViolationScoreContainer container = new (clock);

        // Twice The Expected Rate Nets One Drain's Worth Of Score Per Second, So The Threshold Is Crossed In Well Under Two Minutes
        for (int second = 0; second < 120; second++)
        {
            for (int packet = 0; packet < ViolationScoreContainer.EstimatedPacketsPerSecond * 2; packet++)
                container.ChargeArrival(Source());

            clock.Advance(OneSecond);
            container.Drain();
        }

        await Assert.That(container.IsWithinAllowance(Source())).IsFalse();
    }

    [Test]
    public async Task A_Source_Well_Above_The_Threshold_Is_Actioned()
    {
        ViolationScoreContainer container = new (new ControllableTimeProvider());

        bool actioned = false;

        // Nothing Is Drained, So The Score Only Climbs
        for (int attempt = 0; attempt < 200; attempt++)
        {
            container.ChargeArrival(Source());
            container.ChargeViolation(Source(), ViolationScoreContainer.RateLimitViolationWeight);

            if (container.IsWithinAllowance(Source()) is false)
            {
                actioned = true;

                break;
            }
        }

        await Assert.That(actioned).IsTrue();
    }

    // A Regression Test For An All-Or-Nothing Charge: A Weight That Does Not Divide The Threshold Exactly Must Still Accumulate, Or A Source Can Park Just Below The Threshold Indefinitely
    [Test]
    public async Task A_Weight_That_Does_Not_Divide_The_Threshold_Still_Accumulates()
    {
        ViolationScoreContainer container = new (new ControllableTimeProvider());

        for (int attempt = 0; attempt < 400; attempt++)
            container.ChargeViolation(Source(), ViolationScoreContainer.DuplicateViolationWeight);

        using (Assert.Multiple())
        {
            await Assert.That(container.IsWithinAllowance(Source())).IsFalse();
            await Assert.That(container.Score(Source())).IsGreaterThan(ViolationScoreContainer.ActionableThreshold);
        }
    }

    [Test]
    public async Task An_Actioned_Source_Recovers_After_Draining()
    {
        ControllableTimeProvider clock = new ();
        ViolationScoreContainer container = new (clock);

        while (container.IsWithinAllowance(Source()))
            container.ChargeViolation(Source(), ViolationScoreContainer.RateLimitViolationWeight);

        // A Minute Of Drain At The Configured Rate Is Far More Than The Threshold, So A Source That Stops Must Be Clear Again
        for (int second = 0; second < 60; second++)
        {
            clock.Advance(OneSecond);
            container.Drain();
        }

        await Assert.That(container.IsWithinAllowance(Source())).IsTrue();
    }

    // The Escalation Is What Makes A Persistent Source Expensive To Itself, And It Is The Reason The Maximum Is Separate From The Threshold
    [Test]
    public async Task An_Actioned_Source_That_Keeps_Sending_Climbs_Above_The_Threshold()
    {
        ViolationScoreContainer container = new (new ControllableTimeProvider());

        while (container.IsWithinAllowance(Source()))
            container.ChargeViolation(Source(), ViolationScoreContainer.RateLimitViolationWeight);

        int scoreWhenFirstActioned = container.Score(Source());

        for (int packet = 0; packet < 100; packet++)
            container.ChargeArrival(Source());

        // Each Further Packet Costs Its Own Weight Plus The Actioned Weight, So A Hundred Of Them Climb By Far More Than A Hundred
        await Assert.That(container.Score(Source())).IsGreaterThan(scoreWhenFirstActioned + (100 * ViolationScoreContainer.ActionedViolationWeight));
    }

    [Test]
    public async Task The_Score_Saturates_At_The_Maximum()
    {
        ViolationScoreContainer container = new (new ControllableTimeProvider());

        for (int attempt = 0; attempt < 1000; attempt++)
        {
            container.ChargeArrival(Source());
            container.ChargeViolation(Source(), ViolationScoreContainer.TooShortViolationWeight);
        }

        int maximum = ViolationScoreContainer.MaximumViolationScore;

        await Assert.That(container.Score(Source())).IsEqualTo(maximum);
    }

    // Graduated Persistence: The Reason The Two Ceilings Are Separate Is That A Source Which Keeps Pushing Must Take Longer To Recover Than One Which Stops
    [Test]
    public async Task A_Source_That_Kept_Pushing_Takes_Longer_To_Recover_Than_One_That_Stopped()
    {
        ControllableTimeProvider clock = new ();
        ViolationScoreContainer container = new (clock);

        IPEndPoint stopped = Source(40000);
        IPEndPoint persistent = Source(40001);

        IPEndPoint[] sources = [stopped, persistent];

        foreach (IPEndPoint source in sources)
            while (container.IsWithinAllowance(source))
                container.ChargeViolation(source, ViolationScoreContainer.RateLimitViolationWeight);

        // Only The Persistent Source Keeps Sending While Actioned
        for (int packet = 0; packet < 2000; packet++)
            container.ChargeArrival(persistent);

        for (int second = 0; second < 30; second++)
        {
            clock.Advance(OneSecond);
            container.Drain();
        }

        using (Assert.Multiple())
        {
            await Assert.That(container.IsWithinAllowance(stopped)).IsTrue();
            await Assert.That(container.IsWithinAllowance(persistent)).IsFalse();
        }
    }

    [Test]
    public async Task A_Drain_Removes_Score_In_Proportion_To_Elapsed_Time()
    {
        ControllableTimeProvider clock = new ();
        ViolationScoreContainer container = new (clock);

        container.ChargeViolation(Source(), ViolationScoreContainer.TooShortViolationWeight);

        clock.Advance(TimeSpan.FromSeconds(1));
        container.Drain();

        int afterOneSecond = container.Score(Source());

        container.ChargeViolation(Source(), ViolationScoreContainer.TooShortViolationWeight);
        container.ChargeViolation(Source(), ViolationScoreContainer.TooShortViolationWeight);

        clock.Advance(TimeSpan.FromSeconds(2));
        container.Drain();

        using (Assert.Multiple())
        {
            // 200 Charged, 140 Drained
            await Assert.That(afterOneSecond).IsEqualTo(60);

            // 60 Carried Over Plus 400 Charged, Less Two Seconds Of Drain
            await Assert.That(container.Score(Source())).IsEqualTo(180);
        }
    }

    // Every Other Advance In This Suite Is A Whole Multiple Of 50 Milliseconds, Which Is Exactly When The Drain Is A Whole Number, So Without This The Truncation In "Drain" Is Never Exercised
    [Test]
    public async Task A_Drain_Over_A_Fractional_Interval_Truncates_The_Remaining_Score()
    {
        ControllableTimeProvider clock = new ();
        ViolationScoreContainer container = new (clock);

        container.ChargeViolation(Source(), ViolationScoreContainer.TooShortViolationWeight);

        // 1234 Milliseconds Drains 172.76, So 200 Must Leave 27, Not The 28 A Whole-Number Drain Would Leave
        clock.Advance(TimeSpan.FromMilliseconds(1234));
        container.Drain();

        await Assert.That(container.Score(Source())).IsEqualTo(27);
    }

    // The Reference Waits For Enough Elapsed Time Rather Than Draining A Partial Amount, And Must Not Discard The Remainder When It Does
    [Test]
    public async Task A_Drain_Before_The_Minimum_Interval_Does_Nothing_And_Keeps_The_Remainder()
    {
        ControllableTimeProvider clock = new ();
        ViolationScoreContainer container = new (clock);

        container.ChargeViolation(Source(), ViolationScoreContainer.TooShortViolationWeight);

        clock.Advance(TimeSpan.FromMilliseconds(500));
        container.Drain();

        int afterHalfASecond = container.Score(Source());

        clock.Advance(TimeSpan.FromMilliseconds(500));
        container.Drain();

        using (Assert.Multiple())
        {
            await Assert.That(afterHalfASecond).IsEqualTo(ViolationScoreContainer.TooShortViolationWeight);
            await Assert.That(container.Score(Source())).IsEqualTo(60);
        }
    }

    [Test]
    public async Task A_Source_Drained_To_Zero_Is_Forgotten()
    {
        ControllableTimeProvider clock = new ();
        ViolationScoreContainer container = new (clock);

        container.ChargeViolation(Source(), ViolationScoreContainer.DuplicateViolationWeight);

        clock.Advance(OneSecond);
        container.Drain();

        await Assert.That(container.TrackedSourceCount).IsEqualTo(0);
    }

    // Reading A Source's Standing Happens For Every Datagram, So It Must Not Cause The Source To Be Tracked
    [Test]
    public async Task An_Unknown_Source_Is_Within_Allowance_And_Is_Not_Tracked()
    {
        ViolationScoreContainer container = new (new ControllableTimeProvider());

        using (Assert.Multiple())
        {
            await Assert.That(container.IsWithinAllowance(Source())).IsTrue();
            await Assert.That(container.Score(Source())).IsEqualTo(0);
            await Assert.That(container.TrackedSourceCount).IsEqualTo(0);
        }
    }

    [Test]
    public async Task Sources_Are_Scored_Independently()
    {
        ViolationScoreContainer container = new (new ControllableTimeProvider());

        while (container.IsWithinAllowance(Source(40000)))
            container.ChargeViolation(Source(40000), ViolationScoreContainer.RateLimitViolationWeight);

        await Assert.That(container.IsWithinAllowance(Source(40001))).IsTrue();
    }

    // A Weight At Or Above The Threshold Would Action A Source On A Single Anomaly, Which Is What The Weighting Exists To Prevent
    [Test]
    public async Task Every_Weight_Is_Below_The_Actionable_Threshold()
    {
        int[] weights =
        [
            ViolationScoreContainer.TooShortViolationWeight,
            ViolationScoreContainer.UnauthenticatedViolationWeight,
            ViolationScoreContainer.RateLimitViolationWeight,
            ViolationScoreContainer.DuplicateViolationWeight,
            ViolationScoreContainer.ActionedViolationWeight,
            ViolationScoreContainer.PacketScore
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
    public async Task The_Maximum_Is_Above_The_Actionable_Threshold()
    {
        int maximum = ViolationScoreContainer.MaximumViolationScore;

        await Assert.That(maximum).IsGreaterThan(ViolationScoreContainer.ActionableThreshold);
    }

    [Test]
    public async Task A_Single_Violation_Of_Any_Weight_Never_Actions_A_Source()
    {
        ViolationScoreContainer container = new (new ControllableTimeProvider());

        container.ChargeViolation(Source(41000), ViolationScoreContainer.TooShortViolationWeight);
        container.ChargeViolation(Source(41001), ViolationScoreContainer.UnauthenticatedViolationWeight);
        container.ChargeViolation(Source(41002), ViolationScoreContainer.RateLimitViolationWeight);
        container.ChargeViolation(Source(41003), ViolationScoreContainer.DuplicateViolationWeight);

        using (Assert.Multiple())
        {
            await Assert.That(container.IsWithinAllowance(Source(41000))).IsTrue();
            await Assert.That(container.IsWithinAllowance(Source(41001))).IsTrue();
            await Assert.That(container.IsWithinAllowance(Source(41002))).IsTrue();
            await Assert.That(container.IsWithinAllowance(Source(41003))).IsTrue();
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they fail**

Run: `dotnet build source/COMPEL.slnx`
Expected: FAIL — `ViolationScoreContainer` does not exist.

- [ ] **Step 5: Write the container**

```csharp
namespace COMPEL.Services.Proxy;

/// <summary>
///     Scores abusive behaviour per source address and reports when a source's score is high enough to act upon.
///     Every datagram costs its source a little, every violation costs its weight on top, and a drain proportional to elapsed time removes score again, which reproduces the reference proxy's accumulate-and-decay model: a source within the expected packet rate never accumulates, while one above it does and recovers only once it stops.
///     No state is persisted and nothing is attributed to an account, because at this layer there is only an address and a datagram.
/// </summary>
internal sealed class ViolationScoreContainer(TimeProvider timeProvider)
{
    // "BAN_THRESHOLD": The Score Above Which A Source Is Acted Upon; Named For The Decision Rather Than Today's Action, Which Is A Drop
    internal const int ActionableThreshold = 4000;

    // "MAX_WARN_COUNT": Where This Implementation Saturates Every Path. The Reference Bounds Only The Above-Threshold Escalation With It And Lets Its Own Count Climb Unbounded; Saturating Instead Keeps The Arithmetic In Range And Bounds The Worst Case To Roughly Two And A Half Minutes Of Drain, At The Cost Of A Flood's Penalty No Longer Growing With Its Duration
    internal const int MaximumViolationScore = 20000;

    // "ESTIMATED_PACKETS_PER_SECOND": The Packet Rate A Client Is Expected To Stay Under, Which Is Both The Score Drained Per Second And The Arrival Rate At Which A Source Breaks Even
    internal const int EstimatedPacketsPerSecond = 140;

    // Every Datagram Costs This Much Before Any Violation Weight, Which Is What Makes The Drain Rate Meaningful: A Source Sending Faster Than "EstimatedPacketsPerSecond" Accumulates Without Violating Anything
    internal const int PacketScore = 1;

    // "WARN_TOO_SHORT"
    internal const int TooShortViolationWeight = 200;

    // "WARN_UNAUTHENTICATED"
    internal const int UnauthenticatedViolationWeight = 200;

    // "WARN_LIMIT"
    internal const int RateLimitViolationWeight = 100;

    // "WARN_DUPE"
    internal const int DuplicateViolationWeight = 30;

    // "WARN_BANNED": Charged For Every Datagram From A Source That Is Already Actioned, Which Is What Drives A Persistent Source Towards "MaximumViolationScore"
    internal const int ActionedViolationWeight = 10;

    // The Reference Drains Only Once More Than This Much Time Has Passed, So A Pass That Runs Early Returns Without Advancing Its Mark Rather Than Draining A Partial Amount And Discarding The Remainder
    private static readonly TimeSpan MinimumDrainInterval = TimeSpan.FromMilliseconds(900);

    private readonly ConcurrentDictionary<IPEndPoint, int> scores = new ();

    private long lastDrainTimestamp = timeProvider.GetTimestamp();

    /// <summary>
    ///     How many sources currently carry a score. Exposed for tests and diagnostics; a source drained to zero is no longer counted.
    /// </summary>
    internal int TrackedSourceCount => scores.Count;

    /// <summary>
    ///     Charges <paramref name="source"/> for the arrival of one datagram, whatever it contains, plus <see cref="ActionedViolationWeight"/> if that leaves it over <see cref="ActionableThreshold"/>.
    ///     Called exactly once per datagram, before any check, so that a flood carrying no detectable violation is still scored.
    /// </summary>
    internal void ChargeArrival(IPEndPoint source) => scores.AddOrUpdate(source, Arrived(0), static (_, score) => Arrived(score));

    /// <summary>
    ///     Charges <paramref name="weight"/> against <paramref name="source"/> for a specific violation, on top of the arrival already charged for the same datagram.
    ///     The outcome is read separately through <see cref="IsWithinAllowance"/>, because the score is recorded whether or not the source was already actionable.
    /// </summary>
    internal void ChargeViolation(IPEndPoint source, int weight)
        => scores.AddOrUpdate(source,

            // Both Factories Are Static And Take The Weight As State, So Charging Allocates No Closure On The Datagram Path
            static (_, violationWeight) => Math.Min(violationWeight, MaximumViolationScore),
            static (_, score, violationWeight) => Math.Min(score + violationWeight, MaximumViolationScore),
            weight);

    /// <summary>
    ///     Whether <paramref name="source"/> is still within <see cref="ActionableThreshold"/>. Records nothing and begins tracking nothing, so it is safe to call for every datagram.
    /// </summary>
    internal bool IsWithinAllowance(IPEndPoint source) => Score(source) <= ActionableThreshold;

    /// <summary>
    ///     The current score for <paramref name="source"/>, or zero if it carries none.
    /// </summary>
    internal int Score(IPEndPoint source) => scores.TryGetValue(source, out int score) ? score : 0;

    /// <summary>
    ///     Removes score from every tracked source in proportion to the time elapsed since the last drain, flooring at zero, and forgets any source that reaches it so an address which has stopped misbehaving is not tracked for the life of the process.
    ///     Only one call may be in progress at a time, which the proxy's single maintenance loop satisfies. The elapsed-time mark is unsynchronised, so concurrent calls would lose an update to it and measure the following pass from the wrong point; the per-source writes would not double-drain, because each is conditional on the score it read.
    /// </summary>
    internal void Drain()
    {
        long now = timeProvider.GetTimestamp();
        TimeSpan elapsed = timeProvider.GetElapsedTime(lastDrainTimestamp, now);

        if (elapsed <= MinimumDrainInterval)
            return;

        lastDrainTimestamp = now;

        // The Amount Stays Fractional And The Subtraction's Result Is Truncated, Which Is What The Reference Does: Its Score Is An Unsigned Integer Assigned From A Float Subtraction, So Each Source Rounds Down And Drains Up To One More Than The Exact Amount Rather Than Up To One Less
        // No Score Can Exceed The Maximum, So Clamping The Amount To It Keeps A Long Pause Between Passes From Overflowing The Conversion Below
        double drainAmount = Math.Min(elapsed.TotalSeconds * EstimatedPacketsPerSecond, MaximumViolationScore);

        foreach (KeyValuePair<IPEndPoint, int> entry in scores)
        {
            int remaining = entry.Value > drainAmount ? (int)(entry.Value - drainAmount) : 0;

            // A Source Drained To Zero Is Forgotten, So An Address That Has Stopped Misbehaving Is Not Tracked For The Life Of The Process
            // Both Writes Are Conditional On The Score Not Having Changed Since It Was Read: If A Charge Landed During This Pass, The Source Simply Waits For The Next One, Which Loses A Drain Rather Than A Charge
            if (remaining is 0)
                scores.TryRemove(entry);

            else
                scores.TryUpdate(entry.Key, remaining, entry.Value);
        }
    }

    /// <summary>
    ///     A source's score after one more datagram arrives.
    /// </summary>
    private static int Arrived(int score)
    {
        int arrived = score + PacketScore;

        // A Source Already Over The Threshold Pays Extra For Every Further Datagram, Deliberately: The Reference Does This So That Enforcement Failing Elsewhere Still Leaves A Persistent Source Costed
        if (arrived > ActionableThreshold)
            arrived += ActionedViolationWeight;

        return Math.Min(arrived, MaximumViolationScore);
    }
}
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet build source/COMPEL.slnx && dotnet test source/COMPEL.slnx`
Expected: build succeeds with 0 warnings; all tests pass, the expected-rate test and the accumulation regression test included.

- [ ] **Step 7: Commit**

```bash
git add source/COMPEL/Services/Proxy/ViolationScoreContainer.cs source/COMPEL.Tests/Services/Proxy/ControllableTimeProvider.cs source/COMPEL.Tests/Services/Proxy/ViolationScoreContainerTests.cs
git commit -m "Score Proxy Abuse Per Source With A Draining Accumulator"
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
    // The Check And The Record Below Must Not Be Separable, Or Two Datagrams Carrying The Same Counter Could Both Be Admitted; The Forwarder's Single Receive Loop Makes That Unlikely Rather Than Impossible, And An Uncontended Lock Costs Nothing Against A Datagram's Other Work
    private readonly Lock admissionLock = new ();

    // A Byte Per Admissible Counter Rather Than A Bit, Because Indexing Beats Masking On This Path And The Whole Window Is Under One And A Half Kilobytes At The Largest Quota
    private readonly bool[] seen;

    internal ChallengeWindow(uint challenge, ushort quota)
    {
        Challenge = challenge;
        Quota = quota;
        seen = new bool[quota];
    }

    internal uint Challenge { get; }

    private ushort Quota { get; }

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

        lock (admissionLock)
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
- Consumes: `ProxyForwarderKind`, `ChallengeQuota`, `ClientPacketReader`, `ViolationScoreContainer` (`ChargeArrival`, `ChargeViolation`, `IsWithinAllowance`, `Drain`; not `IDisposable`), `ChallengeWindow`, `ChallengeAdmission`.
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
        // "PublicPort" Is Whatever Was Passed In Rather Than The Port The Socket Ended Up Bound To, So It Must Be A Real Port Chosen Up Front, Exactly As The Other Tests In This File Do
        int publicPort = FreeUDPPort();

        ViolationScoreContainer container = new (TimeProvider.System);

        using Socket server = new (AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        server.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        // The Null-Forgiving Operator Is Banned, And This File Already Has The Idiom For This
        int localPort = server.LocalEndPoint is IPEndPoint bound ? bound.Port : throw new InvalidOperationException("Could Not Determine The Bound UDP Port");

        using UDPForwarder forwarder = new (publicPort, localPort, ProxyForwarderKind.Game, TimeSpan.FromSeconds(10), container, NullLogger.Instance);

        using CancellationTokenSource cancellation = new (TimeSpan.FromSeconds(5));

        _ = forwarder.Run(cancellation.Token);

        using Socket client = new (AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        client.SendTo(new byte[8], new IPEndPoint(IPAddress.Loopback, publicPort));

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
    private readonly ViolationScoreContainer scoreContainer;
    private readonly ushort packetQuota;
    private readonly ushort gameCommandQuota;

    private int droppedDatagramCount;

    public int DroppedDatagramCount => Volatile.Read(ref droppedDatagramCount);
```

Set them in the constructor, after the existing assignments:

```csharp
        this.kind = kind;
        this.scoreContainer = scoreContainer;

        // The Interval Is Not Stored: It Is Only Needed To Derive The Two Quotas, Which Are Fixed For The Life Of The Forwarder
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
            // Every Datagram Costs Its Source, Whatever It Turns Out To Contain, Which Is What The Drain Rate Is Calibrated Against; The Reference Does This First As Well
            scoreContainer.ChargeArrival(client);

            // A Source Already Over The Threshold Is Refused Before Anything Reads Its Datagram
            if (scoreContainer.IsWithinAllowance(client) is false)
            {
                Drop(client, "Actioned");

                continue;
            }

            ReadOnlySpan<byte> datagram = buffer.AsSpan(0, result.ReceivedBytes);

            // The Length Guard Runs Before Any Field Is Read So None Is Ever Read Out Of Range
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

            try { await session.UpstreamSocket.SendAsync(buffer.AsMemory(0, result.ReceivedBytes), SocketFlags.None, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (Exception exception) { logger.LogDebug(exception, "Failed To Forward Datagram To Server For {Client}", client); }
```

Add the drop helpers, which log only the first drop per source so a flood cannot become a log flood. There are two because the arrival has already been charged for every datagram by the time a check fails: the overload taking a weight charges that violation on top, while the overload without one charges nothing further.

```csharp
    private void Drop(IPEndPoint client, int weight, string reason)
    {
        scoreContainer.ChargeViolation(client, weight);

        Drop(client, reason);
    }

    private void Drop(IPEndPoint client, string reason)
    {
        Interlocked.Increment(ref droppedDatagramCount);

        if (reportedDrops.TryAdd(client, true))
            logger.LogWarning("Dropped A Datagram From {Client} On Public Port {Port} ({Reason}); Further Drops From This Source Are Not Logged", client, PublicPort, reason);
    }
```

with `private readonly ConcurrentDictionary<IPEndPoint, bool> reportedDrops = new ();` beside the other fields.

- [ ] **Step 5: Change the proxy service to pass the kind and the container**

In `UDPProxyService`, add a `private readonly ViolationScoreContainer scoreContainer = new (TimeProvider.System);` field and call `scoreContainer.Drain();` once per iteration of `RunMaintenanceLoop`. The container is not `IDisposable` and must not be disposed alongside the forwarders.

**That loop ticks on `ChallengeRenewalInterval`, which is ten seconds, not one.** Do not change it, and do not add a second loop: the drain removes score in proportion to the time actually elapsed, so a ten-second pass removes ten seconds' worth and the result is the same as a one-second pass would give. This is the property that made a drain of a fixed amount per call unacceptable — a fixed 140 per pass on a ten-second tick would drain fourteen times too slowly, and a client sending at exactly the expected rate would accumulate about 1260 score every ten seconds and be actioned within about half a minute. The only cost of the slower tick is granularity: an actioned source stays actioned until the next pass. Then change the two calls to pass the enum, and change `TryAddForwarder`:

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
- Modify: `source/COMPEL/Services/Proxy/UDPProxyService.cs` (the aggregate count, the under-attack indicator, and the deferred-work TODO)
- Modify: `source/COMPEL/Endpoints/Contracts.cs`
- Modify: `source/COMPEL/Endpoints/ControlPlaneEndpoints.cs`

**Interfaces:**
- Consumes: `UDPForwarder.DroppedDatagramCount`.
- Produces: `int UDPProxyService.DroppedDatagramCount`; `bool UDPProxyService.IsUnderAttack`; `ProxyDroppedDatagramCount` and `ProxyIsUnderAttack` members on `StatusResponse`.

- [ ] **Step 1: Fact verification**

```bash
grep -n "ProxyFailedForwarderCount" source/COMPEL/Endpoints/Contracts.cs source/COMPEL/Endpoints/ControlPlaneEndpoints.cs
grep -n "FailedForwarderCount\|forwarders\|TODO" source/COMPEL/Services/Proxy/UDPProxyService.cs
```

Expected: the existing failed-forwarder member shows the pattern the new member follows; the existing TODO is the one to extend with the deferred watermark work; and, importantly, `FailedForwarderCount` is `Volatile.Read` of a plain `int` field rather than anything computed from `forwarders`.

**That last point is a constraint, not trivia.** `forwarders` is a plain `List<UDPForwarder>`, added to during start-up and `Clear()`ed on shutdown, with no synchronisation. Enumerating it from the control-plane thread — which is what a `Sum` over it would do — throws `InvalidOperationException` if a request lands during either, turning `/status` into a 500. The existing member sidesteps this by publishing a field, and the new one must do the same.

- [ ] **Step 2: Expose the aggregate count**

In `UDPProxyService`, beside `failedForwarderCount`, following the same publish-a-field pattern for the same reason:

```csharp
    private int droppedDatagramCount;

    /// <summary>
    ///     The number of client datagrams the proxy has refused to relay, across every forwarder, as at the last maintenance pass.
    /// </summary>
    public int DroppedDatagramCount => Volatile.Read(ref droppedDatagramCount);
```

The aggregate is summed inside `RunMaintenanceLoop`, which already iterates `forwarders` on the only thread that may. Extend that existing loop body rather than adding a second pass over the collection:

```csharp
            int droppedDatagrams = 0;

            foreach (UDPForwarder forwarder in forwarders)
            {
                forwarder.ChallengeActiveSessions();
                forwarder.EvictIdleSessions(IdleSessionTimeout);

                droppedDatagrams += forwarder.DroppedDatagramCount;
            }

            Volatile.Write(ref droppedDatagramCount, droppedDatagrams);
```

The reported count is therefore at most one maintenance pass stale, which is what a status endpoint wants anyway.

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

The reference keeps a global counter raised on enforcement events and reset periodically, so a sustained attack is distinguishable from ordinary background noise. Confirm the constant and the reset first:

```bash
P="source/COMPEL/bin/Publish/HoN_Proxy/HoN/branches/retail/Tool/HoNProxy/main.cpp"
grep -nE "define UNDER_ATTACK_THRESHOLD" "$P"
grep -n "under_attack_indicator" "$P"
sed -n '1247,1254p' "$P"
```

Expected: `UNDER_ATTACK_THRESHOLD 1000`; the indicator raised by varying amounts at many sites (`+= 100` for most enforcement events, `+= 10000` for one severe case, `++` and `+= 2` for lesser ones); and a reset to zero on the `++clearUnusedPlayers > 5 * 60` branch, logging the indicator first if it is over the threshold.

**The reference's weights do not port and must not be copied.** They are attached to firewall bans, hardware-identifier bans and connection-table exhaustion — enforcement COMPEL deliberately does not implement, since its response is a local drop. COMPEL's single enforcement event is a refused datagram, so it carries the reference's lightest weight, one, and the threshold stays 1000: a thousand refusals inside one window. Keep the reference's five-minute window, because that is what the threshold was chosen against.

This indicator answers "how much are we refusing", not "how many distinct sources are we refusing". One source that is already actioned and keeps sending can trip it on its own. That is accepted here rather than worked around: the score container knows which sources are actioned, so a distinct-source discriminator can be added later without changing this counter.

In `UDPProxyService`, beside the existing fields:

```csharp
    // "UNDER_ATTACK_THRESHOLD": Refusals Within One Window Above Which The Proxy Reports Itself Under Attack
    private const int UnderAttackThreshold = 1000;

    // The Reference Resets Its Indicator Every Five Minutes Of Its Own Housekeeping Tick; Derived From The Interval Rather Than Written Down Twice, So It Stays Five Minutes If The Interval Changes
    private static readonly int UnderAttackWindowPasses = (int) Math.Max(1, TimeSpan.FromMinutes(5).Ticks / ChallengeRenewalInterval.Ticks);

    private int maintenancePassesThisWindow;
    private int droppedDatagramsAtWindowStart;
    private bool isUnderAttack;

    /// <summary>
    ///     Whether the proxy refused more datagrams in the last completed window than the under-attack threshold allows.
    /// </summary>
    public bool IsUnderAttack => Volatile.Read(ref isUnderAttack);
```

In `RunMaintenanceLoop`, after the `Volatile.Write` of the aggregate from Step 2 — so the window judges a freshly summed count — and alongside the `scoreContainer.Drain();` call added in Task 5, close the window once enough passes have elapsed and judge the refusals counted in it:

```csharp
            if (++maintenancePassesThisWindow >= UnderAttackWindowPasses)
            {
                maintenancePassesThisWindow = 0;

                int droppedDatagrams = DroppedDatagramCount;
                int droppedThisWindow = droppedDatagrams - droppedDatagramsAtWindowStart;

                droppedDatagramsAtWindowStart = droppedDatagrams;

                Volatile.Write(ref isUnderAttack, droppedThisWindow > UnderAttackThreshold);

                if (droppedThisWindow > UnderAttackThreshold)
                    logger.LogWarning("The Proxy Refused {DroppedDatagrams} Datagram(s) In The Last Window, Which Exceeds The Under-Attack Threshold Of {Threshold}", droppedThisWindow, UnderAttackThreshold);
            }
```

Only the maintenance loop touches the window counters, so they need no interlocking; `isUnderAttack` is written with `Volatile` because the control plane reads it from another thread. Sampling the aggregate count rather than incrementing per refusal keeps the datagram path free of another shared counter.
- [ ] **Step 5: Record the deferred work**

Extend the existing TODO above `UDPProxyService` so the deferred checks are documented where the next reader will look:

```csharp
// TODO: The Proxy Validates Datagram Length, The Per-Challenge Packet Quota, And Duplicate Counters, And Scores Abuse Per Source; It Does Not Yet Validate The Watermarks
// TODO: The Reference Proxy Also Checks A Constant Per-Region Watermark And A Dynamic CRC32C One, Which Together Are Its Anti-Cheat Signal; Adding Them Needs A Region Setting COMPEL Has No Equivalent For, And Carries A Higher False-Positive Cost Than The Checks Above
// TODO: Challenge Values Are A Monotonic Counter Rather Than The Reference's Cryptographically Random One, So They Are Guessable; A Source That Guesses One Is Held To The Per-Challenge Quota Instead Of The Much Smaller Unauthenticated One, And Watermark Validation Would Depend On Them Being Unpredictable
// TODO: Making Them Random Means Separating The Challenge From The Creation Timestamp, Which Currently Share One Value In "BuildChallengePacket", So It Is Deliberately Left Alone Here Rather Than Changed On A Path That Works In Production
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
- [ ] `dotnet test source/COMPEL.slnx` passes, with at least 39 new tests across the six new units.
- [ ] `scripts/Publish-Native-AOT-Release.ps1` succeeds with no trim or AOT warnings.
- [ ] A real match through the proxy shows `Disconnects(0)`, a drop count of zero, and `proxyIsUnderAttack` false.
- [ ] No file uses `var`, an abbreviation, American spelling, or the null-forgiving operator.
- [ ] Every new constant carries the reference `#define` name in a trailing comment.
