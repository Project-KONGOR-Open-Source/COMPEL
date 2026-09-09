# Review Remediation And Restructure Implementation Plan

## Status: Executed, With Loose Ends Outstanding

**This document is committed temporarily and is meant to be deleted.** It is here only so the loose ends below stay visible until they are addressed. Delete it, and this section with it, once all four are closed.

Every task in this plan is implemented, reviewed, and committed. The suite passes at 65 tests with no build warnings, and a Native AOT publish succeeds. Four things remain open.

**Two checks are owed before the next release.** Neither could be run during execution, because both need an elevated console and the live CDN. They are the only real verification of the synchronisation log vocabulary and the distribution version fallback, which this work rewrote most heavily, and no automated test covers either. See "Owed Before Release" below.

**Two decisions are deferred to the operator.** Both are WILLOWMAKER-parity trade-offs where COMPEL's deployment shape differs from a launcher's: synchronisation writes one log line per up-to-date file on every restart into a log that is never rotated, and start-up blocks for up to thirty seconds on a machine with no network when synchronisation is disabled. Changing either departs from parity, so neither was changed. See the last two entries under "Deferred By Decision" below.

Everything else in "Deferred By Decision" is a settled decision that needs no further action; those entries stay for the record and are not loose ends.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix the defects found in the 2026-09-09 review, bring every file into line with the agent instructions, remove the banner while keeping the log session header, bring logging and content synchronisation to parity with WILLOWMAKER, and restructure both projects so no folder mixes loose files with sub-folders.

**Architecture:** Logging moves first: WILLOWMAKER's `Logger` and `LogCategory` are ported, a small `ILoggerProvider` bridges the hosted services' `ILogger<T>` calls into that logger, and Serilog is removed. Structural moves follow so every later task references final paths. Synchronisation is then aligned with WILLOWMAKER's log vocabulary and failure handling, the behavioural fixes land with a failing test each where testable, and two mechanical sweeps close the plan.

**Tech Stack:** .NET 11, ASP.NET Core minimal APIs published with Native AOT, TUnit on the Microsoft Testing Platform, `dotnet` CLI, Git Bash.

**Spec:** The review delivered in this session, the user's structural requests, and the parity requirement against `C:\Users\SADS-810\Source\WILLOWMAKER`, all restated under Requirements below so this plan is self-contained.

## Requirements

Structural requests from the user:

1. Remove the banner art. Keep the log session header.
2. Move `source/COMPEL/SingleInstanceGuard.cs` into an appropriate namespace.
3. No folder may contain loose files alongside sub-folders. `source/COMPEL/Services/` is the cited example; the project root has the same problem.
4. Structure the tests into directories.
5. Log generation must match WILLOWMAKER as closely as possible.
6. Content synchronisation must match WILLOWMAKER as closely as possible, with GUI semantics transformed into terminal semantics.
7. Nothing is committed. The user will stage slices with the assistant afterwards and receive a recommended commit message per slice.

What WILLOWMAKER does, for reference:

- `Utilities/Logger.cs` appends to one log file. Its constructor writes a session header, preceded by a blank line when the file already has content. Each entry is `[{DateTime.Now:O}] [{category}] {message}`; there are no levels.
- `Constants/LogCategory.cs` defines eleven-character underscore-padded categories: `COMMAND____`, `EXECUTABLE_`, `GUARD______`, `INITIALISE_`, `PARAMETERS_`, `SYNCHRONISE`, `UPDATE_____`, `VERSION____`.
- Start-up logs the guard verdict under `GUARD`, then `Current Version: v2.1.3`, `Checking For Updates ...`, `WILLOWMAKER Is Up To Date` or `Update Available: ...` under `VERSION`, and the update decision and progress under `UPDATE`.
- Synchronisation logs `INIT: Fetching Manifest For Variant "wac" From CDN`, `INIT: Manifest Version {v} Lists {n} File(s)`, `PLAN: {plan}`, then one line per file: `PULL:`, `NUKE:`, `SKIP:`, `FAIL:`, and finally `DONE: {summary}`. Failures log `FAIL: {n} File(s) Failed To Be Transferred :: Launch Aborted`, `FAIL: CDN Unreachable :: HTTP {status}`, or `FAIL: {Type} :: {Message}`. Skips log `SKIP: Synchronisation Skipped (Manual Override)` or `(Unsafe Location)`.
- After a failed synchronisation it identifies the processes holding the failed files with `Utilities/FileLockDetector.cs` and shows them in a dialog, grouped by application with a process count.
- Progress is a progress bar and four status columns. Those are GUI semantics; the per-file log lines are the terminal equivalent, so COMPEL's throttled byte-count progress line goes.

Defects from the review that this plan fixes:

- The Linux orphan sweep truncates the lookup name to fifteen characters, but .NET compares against the untruncated name, so the sweep never matches on Linux.
- Whitespace in `UserName` or `Password` is accepted, yet the manager's `Set` command drops the final whitespace-delimited token, so such credentials fail silently.
- A lowercase `Location` passes validation but is forwarded to the manager unchanged.
- The ping responder advertises an empty version whenever the distribution is not synchronised, and the HON client discards servers whose version does not match its own.
- After a failed manager launch the supervisor keeps a reference to a disposed `Process`, producing spurious warnings on later stop and restart requests.
- The Windows update script embeds paths in single-quoted PowerShell literals without escaping apostrophes.
- An unreadable `COMPEL.json` crashes with a stack trace instead of the clean configuration error.
- The Gateway and Location descriptions in `COMPEL.json` omit options the validator accepts, and the options class documents the wrong file name and a different default location.

Agent-instruction deviations that this plan fixes:

- 103 Start Case comments across 19 files end in a full stop or contain sentence breaks.
- The TODO in `UDPProxyService.cs` is in sentence case and sits between the XML summary and the class.
- Symbol references in three comments are not enclosed in double quotes; two comments carry drift-prone technical details.
- Five comments describe obvious code.
- One console message and one MSBuild comment are in sentence case.
- Two identifiers use abbreviations: `info` and `tempDirectory`.
- One test uses the null-forgiving operator.
- The test project keeps its global usings in `GlobalUsings.cs` rather than `Internals/UsingDirectives.cs`.
- `COMPEL.cs` keeps two local using directives for Serilog. Serilog itself goes in Task 2, which resolves this.

## Global Constraints

- Never commit. Every task ends with a build and a test run, and the changes stay in the working tree. The Staging Slices section at the end lists the intended slices.
- Never use `var`; always write explicit type names.
- Acronyms stay upper case in PascalCase and in camelCase except at the start of a symbol: `UDPForwarder`, `baseURL`, `httpClient`, `eventID`.
- No abbreviations in symbol names; no single-letter or abbreviated lambda parameters.
- Four-space indentation, CRLF line endings, every file terminated with a newline, no tabs.
- Code comments use Start Case and never end with punctuation; a multi-sentence comment is either joined with semicolons on one line or split one sentence per line.
- XML documentation uses sentence case with a full stop at the end of each sentence; a sentence never spans two lines.
- Symbol names, file names, and flags inside comments are enclosed in double quotes; in XML documentation prefer `<see cref="..."/>` without parameter lists.
- British English throughout: synchronise, serialise, artefact, behaviour, initialise.
- No null-forgiving operator outside the two permitted cases.
- Global using directives live in `Internals/UsingDirectives.cs` in each project root, sorted lexicographically ascending and grouped by root namespace.
- Namespaces must match folder structure; `IDE0130` is configured as an error in `source/.editorconfig`.
- Test method names use an underscore between every word: `A_Password_Containing_Whitespace_Is_Rejected`.
- Log messages written through the logger carry no trailing full stop, matching WILLOWMAKER's log lines.
- `source/COMPEL/Services/ContentBroker/ContentBroker.cs` must remain identical to `C:\Users\SADS-810\Source\WILLOWMAKER\source\WILLOWMAKER.Core\Services\ContentBroker\ContentBroker.cs` except for the namespace line, the User-Agent product name, and the one existing word difference in the class summary. Any edit to one copy is applied to both and verified with `git diff --no-index`.
- `source/COMPEL/Utilities/FileLockDetector.cs`, once ported, must remain identical to WILLOWMAKER's copy except for the namespace line.
- The release workflow runs the published binary once to generate the shipped `COMPEL.json` and fails if a `COMPEL.log` or `COMPEL.lock` is left behind, so the first-run path must exit before the logger is constructed.
- Build with `dotnet build source/COMPEL.slnx` and test with `dotnet test source/COMPEL.slnx`, both from the repository root. The suite has 52 tests at the start of this plan and must end with 65.
- All shell commands below assume Git Bash with the repository root `C:\Users\SADS-810\Source\COMPEL` as the working directory.

## Target Layout

The entry point and the project file stay at the project root because the project file has to live there and the entry point conventionally sits beside it. Every other file lives in a folder. If you want the entry point moved as well, say so before Task 3 runs and it will go to `source/COMPEL/Internals/COMPEL.cs`.

```
source/COMPEL/
    COMPEL.cs
    COMPEL.csproj
    Assets/compel.ico
    Configuration/           (unchanged)
    Constants/               LogCategory.cs
    Endpoints/               (unchanged)
    Internals/UsingDirectives.cs
    Properties/              (unchanged)
    Serialisation/           (unchanged)
    Services/
        ContentBroker/       (unchanged)
        Deployment/          DeploymentManifest.cs, LocationGuard.cs, LocationSafetyVerdict.cs, SingleInstanceGuard.cs
        Maintenance/         (unchanged)
        Ping/                (unchanged)
        Proxy/               (unchanged)
        Supervision/         (unchanged)
        Updates/             UpdateGate.cs, VersionCheckResult.cs, VersionChecker.cs
    Utilities/               FileLockDetector.cs, Logger.cs, LoggerProvider.cs, SynchronousProgress.cs

source/COMPEL.Tests/
    COMPEL.Tests.csproj
    Internals/UsingDirectives.cs
    Configuration/           CompelConfigurationLoaderTests.cs, MatchServerManagerOptionsValidatorTests.cs
    Constants/               LogCategoryTests.cs
    Services/
        Deployment/          LocationGuardTests.cs
        Ping/                UDPPingResponderTests.cs
        Proxy/               UDPForwarderTests.cs
        Supervision/         AddressResolverTests.cs, ArtefactsLocatorTests.cs, ManagerArgumentsTests.cs, PortPlanTests.cs
        Synchronisation/     ContentBrokerTests.cs
        Updates/             VersionCheckerTests.cs
    Utilities/               FileLockDetectorTests.cs, LoggerProviderTests.cs, LoggerTests.cs
```

`Constants/` and `Utilities/` mirror WILLOWMAKER's folders of the same names. `SingleInstanceGuard` joins `DeploymentManifest` and `LocationGuard` because all three describe how a COMPEL deployment occupies its directory: the file names it owns, whether the directory is safe to mirror into, and whether another instance already holds it.

The test folder for the content broker is named `Synchronisation`, not `ContentBroker`. A namespace `COMPEL.Tests.Services.ContentBroker` would make the simple name `ContentBroker` resolve to that namespace inside the test class, hiding the static class under test.

## Logging Design

- `Logger` is WILLOWMAKER's class with one terminal transformation: each entry is also written to the console, since the console is where WILLOWMAKER's UI log panel would be. The session header text is COMPEL's existing marker, `▝▚▞▚▞▚▞▚▖ COMPEL Session Started At {timestamp} ▗▞▚▞▚▞▚▞▘`, written to the file only, exactly as WILLOWMAKER writes its header. The log file stays beside the executable.
- `LogCategory` keeps WILLOWMAKER's names and width and adds the categories a match server host needs: `PROXY______`, `PING_______`, `MAINTENANCE`, `CONTROL____`, and `HOST_______` as the fallback. `COMMAND____` and `PARAMETERS_` are not carried over because COMPEL never logs the manager command line: the `-execute` payload contains the account password.
- `LoggerProvider` implements `ILoggerProvider` so the hosted services keep their `ILogger<T>` calls unchanged. It maps the logger name, which is the logging type's full name, to a category, formats the message, and appends an exception as `:: {Type} :: {Message}` the way WILLOWMAKER records failures. Level filtering stays in the host's logging configuration, so the proxy's debug lines never reach the file.
- Serilog and its three packages go, along with request logging, which has no WILLOWMAKER counterpart. Kestrel's "Now listening on" and the lifetime's "Application started" lines still arrive under `INITIALISE_`.
- Every start-up message after the first-run check goes through the logger, so the log file records the guard verdict, the version check, and the update decision as WILLOWMAKER's does. The interactive update prompt, its countdown, and the download percentage stay console-only: they are transient terminal output, not log entries.

---

### Task 1: Introduce The Logger, Its Categories, And The Provider

**Files:**
- Create: `source/COMPEL/Constants/LogCategory.cs`
- Create: `source/COMPEL/Utilities/Logger.cs`
- Create: `source/COMPEL/Utilities/LoggerProvider.cs`
- Create: `source/COMPEL/Utilities/SynchronousProgress.cs`
- Modify: `source/COMPEL/Internals/UsingDirectives.cs`
- Modify: `source/COMPEL.Tests/GlobalUsings.cs`
- Test: `source/COMPEL.Tests/Constants/LogCategoryTests.cs`
- Test: `source/COMPEL.Tests/Utilities/LoggerTests.cs`
- Test: `source/COMPEL.Tests/Utilities/LoggerProviderTests.cs`

**Interfaces:**
- Consumes: `DeploymentManifest.ApplicationName`.
- Produces: `public sealed class Logger` with `Logger(string filePath)` and `void Log(string category, string message)`; `public static class LogCategory` with the eleven constants below and `static string Resolve(string loggerName)`; `internal sealed class LoggerProvider(Logger logger) : ILoggerProvider`; `internal sealed class SynchronousProgress<T>(Action<T> handler) : IProgress<T>`. Nothing is wired up yet; Task 2 does that.

- [x] **Step 1: Add the new namespaces to both global-using files**

In `source/COMPEL/Internals/UsingDirectives.cs`, insert `global using COMPEL.Constants;` after `global using COMPEL.Configuration;` and `global using COMPEL.Utilities;` after `global using COMPEL.Services.Supervision;`.

In `source/COMPEL.Tests/GlobalUsings.cs`, insert `global using COMPEL.Constants;` after `global using COMPEL.Configuration;`, `global using COMPEL.Services.Maintenance;` after `global using COMPEL.Services.ContentBroker;`, `global using COMPEL.Utilities;` after `global using COMPEL.Services.Supervision;`, `global using Microsoft.Extensions.Logging;` before `global using Microsoft.Extensions.Logging.Abstractions;`, `global using System.Reflection;` after `global using System.Net.Sockets;`, and `global using System.Text.RegularExpressions;` after `global using System.Text;`.

- [x] **Step 2: Write the failing tests**

Create `source/COMPEL.Tests/Constants/LogCategoryTests.cs`:

```csharp
namespace COMPEL.Tests.Constants;

/// <summary>
///     Verifies the category constants share one width and that logger names resolve to the categories the log displays.
/// </summary>
public sealed class LogCategoryTests
{
    [Test]
    public async Task Every_Category_Is_Eleven_Characters_Wide()
    {
        IEnumerable<string> categories = typeof(LogCategory)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral)
            .Select(field => (string?) field.GetRawConstantValue() ?? string.Empty);

        using (Assert.Multiple())
        {
            foreach (string category in categories)
                await Assert.That(category.Length).IsEqualTo(11);
        }
    }

    [Test]
    public async Task The_Synchronisation_Service_Resolves_To_The_Synchronise_Category()
    {
        await Assert.That(LogCategory.Resolve(typeof(DistributionSynchronisationService).FullName ?? string.Empty)).IsEqualTo(LogCategory.Synchronise);
    }

    [Test]
    public async Task The_Hosting_Lifetime_Resolves_To_The_Initialise_Category()
    {
        await Assert.That(LogCategory.Resolve("Microsoft.Hosting.Lifetime")).IsEqualTo(LogCategory.Initialise);
    }

    [Test]
    public async Task An_Unrecognised_Logger_Name_Resolves_To_The_Host_Category()
    {
        await Assert.That(LogCategory.Resolve("Some.Unrelated.Type")).IsEqualTo(LogCategory.Host);
    }
}
```

Create `source/COMPEL.Tests/Utilities/LoggerTests.cs`:

```csharp
namespace COMPEL.Tests.Utilities;

/// <summary>
///     Verifies the log file layout: a session header opens each session, sessions are separated by a blank line, and entries carry a timestamp and a fixed-width category.
/// </summary>
public sealed class LoggerTests
{
    private const string SessionMarker = "COMPEL Session Started At";

    private static string TemporaryLogPath() => Path.Combine(Path.GetTempPath(), $"compel-log-{Guid.NewGuid():N}.log");

    [Test]
    public async Task A_Fresh_Log_File_Opens_With_A_Session_Header()
    {
        string path = TemporaryLogPath();

        try
        {
            _ = new Logger(path);

            string content = await File.ReadAllTextAsync(path);

            using (Assert.Multiple())
            {
                await Assert.That(content.StartsWith('▝')).IsTrue();
                await Assert.That(content.Contains(SessionMarker)).IsTrue();
            }
        }

        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task A_Second_Session_Is_Separated_From_The_First_By_A_Blank_Line()
    {
        string path = TemporaryLogPath();

        try
        {
            _ = new Logger(path);
            _ = new Logger(path);

            string content = await File.ReadAllTextAsync(path);

            int markerCount = (content.Length - content.Replace(SessionMarker, string.Empty).Length) / SessionMarker.Length;

            using (Assert.Multiple())
            {
                await Assert.That(markerCount).IsEqualTo(2);
                await Assert.That(content.Contains(Environment.NewLine + Environment.NewLine + "▝")).IsTrue();
            }
        }

        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task An_Entry_Carries_A_Timestamp_And_Its_Category()
    {
        string path = TemporaryLogPath();

        try
        {
            Logger logger = new (path);

            logger.Log(LogCategory.Synchronise, "PLAN: 1 To Download");

            string[] lines = await File.ReadAllLinesAsync(path);

            await Assert.That(Regex.IsMatch(lines[^1], @"^\[\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d+[+-]\d{2}:\d{2}\] \[SYNCHRONISE\] PLAN: 1 To Download$")).IsTrue();
        }

        finally
        {
            File.Delete(path);
        }
    }
}
```

Create `source/COMPEL.Tests/Utilities/LoggerProviderTests.cs`:

```csharp
namespace COMPEL.Tests.Utilities;

/// <summary>
///     Verifies that the hosted services' logger calls land in the log under their mapped category, with an exception recorded as its type and message.
/// </summary>
public sealed class LoggerProviderTests
{
    private static string TemporaryLogPath() => Path.Combine(Path.GetTempPath(), $"compel-log-{Guid.NewGuid():N}.log");

    [Test]
    public async Task A_Hosted_Service_Logger_Writes_Under_Its_Mapped_Category()
    {
        string path = TemporaryLogPath();

        try
        {
            Logger logger = new (path);

            using ILoggerFactory factory = LoggerFactory.Create(builder => builder.AddProvider(new LoggerProvider(logger)));

            factory.CreateLogger<UDPProxyService>().LogInformation("Proxy Forwarding {Instances} Instance(s)", 2);

            string[] lines = await File.ReadAllLinesAsync(path);

            await Assert.That(lines[^1].EndsWith("] [PROXY______] Proxy Forwarding 2 Instance(s)")).IsTrue();
        }

        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task An_Exception_Is_Appended_As_Its_Type_And_Message()
    {
        string path = TemporaryLogPath();

        try
        {
            Logger logger = new (path);

            using ILoggerFactory factory = LoggerFactory.Create(builder => builder.AddProvider(new LoggerProvider(logger)));

            factory.CreateLogger<MaintenanceService>().LogWarning(new IOException("Disc Full"), "Replay Cleanup Failed");

            string[] lines = await File.ReadAllLinesAsync(path);

            await Assert.That(lines[^1].EndsWith("] [MAINTENANCE] Replay Cleanup Failed :: IOException :: Disc Full")).IsTrue();
        }

        finally
        {
            File.Delete(path);
        }
    }
}
```

- [x] **Step 3: Run the build to verify the tests fail**

Run: `dotnet build source/COMPEL.slnx`
Expected: compile errors in the test project, `Logger`, `LogCategory`, and `LoggerProvider` do not exist.

- [x] **Step 4: Create the categories**

Create `source/COMPEL/Constants/LogCategory.cs`:

```csharp
namespace COMPEL.Constants;

/// <summary>
///     Defines the available log entry categories, padded to a fixed width so the category column aligns in the log.
///     The first six mirror WILLOWMAKER's categories; the remainder cover the services a match server host runs that a launcher does not.
/// </summary>
public static class LogCategory
{
    public const string Control     = "CONTROL____";
    public const string Executable  = "EXECUTABLE_";
    public const string Guard       = "GUARD______";
    public const string Host        = "HOST_______";
    public const string Initialise  = "INITIALISE_";
    public const string Maintenance = "MAINTENANCE";
    public const string Ping        = "PING_______";
    public const string Proxy       = "PROXY______";
    public const string Synchronise = "SYNCHRONISE";
    public const string Update      = "UPDATE_____";
    public const string Version     = "VERSION____";

    /// <summary>
    ///     Maps a logger name, which for the hosted services is the full name of the type that logs, to the category shown in the log.
    ///     The hosting lifetime folds into the initialisation category, the web host into the control-plane category, and anything unrecognised is attributed to the host.
    /// </summary>
    public static string Resolve(string loggerName)
    {
        if (loggerName.StartsWith("Microsoft.Hosting.Lifetime", StringComparison.Ordinal))
            return Initialise;

        if (loggerName.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal))
            return Control;

        string typeName = loggerName[(loggerName.LastIndexOf('.') + 1)..];

        return typeName switch
        {
            nameof(DistributionSynchronisationService) => Synchronise,
            nameof(MatchServerManagerSupervisor)       => Executable,
            nameof(AddressResolver)                    => Executable,
            nameof(UDPProxyService)                    => Proxy,
            nameof(UDPPingResponder)                   => Ping,
            nameof(MaintenanceService)                 => Maintenance,
            _                                          => Host
        };
    }
}
```

- [x] **Step 5: Create the logger, the provider, and the synchronous progress reporter**

Create `source/COMPEL/Utilities/Logger.cs`:

```csharp
namespace COMPEL.Utilities;

/// <summary>
///     Writes timestamped, categorised entries to the console and to the log file on disc, in the format WILLOWMAKER uses.
///     Constructing the logger appends a session header, preceded by a blank line when the file already has content, so successive sessions read as distinct blocks.
/// </summary>
public sealed class Logger
{
    private string FilePath { get; }
    private Lock FileLock { get; } = new ();

    public Logger(string filePath)
    {
        FilePath = filePath;

        bool hasExistingContent = File.Exists(FilePath) && new FileInfo(FilePath).Length > 0;

        string sessionSeparator = hasExistingContent ? Environment.NewLine : string.Empty;

        File.AppendAllText(FilePath, sessionSeparator + $"▝▚▞▚▞▚▞▚▖ {DeploymentManifest.ApplicationName} Session Started At {DateTime.Now:O} ▗▞▚▞▚▞▚▞▘" + Environment.NewLine);
    }

    /// <summary>
    ///     Formats a timestamped log entry and writes it to the console and to the log file.
    /// </summary>
    public void Log(string category, string message)
    {
        string entry = $"[{DateTime.Now:O}] [{category}] {message}";

        lock (FileLock)
        {
            Console.WriteLine(entry);

            File.AppendAllText(FilePath, entry + Environment.NewLine);
        }
    }
}
```

Create `source/COMPEL/Utilities/LoggerProvider.cs`:

```csharp
namespace COMPEL.Utilities;

/// <summary>
///     Bridges the host's logging abstraction to <see cref="Logger"/>, so the hosted services keep using <see cref="ILogger{TCategoryName}"/> while every entry lands in the same console and file format as the start-up messages.
///     Level filtering stays with the host's logging configuration; this provider formats whatever reaches it.
/// </summary>
internal sealed class LoggerProvider(Logger logger) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new CategoryLogger(logger, LogCategory.Resolve(categoryName));

    public void Dispose()
    {
    }

    private sealed class CategoryLogger(Logger logger, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel is not LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventID, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            string message = formatter(state, exception);

            // WILLOWMAKER Records A Failure As The Exception's Type And Message On The Same Line, So The Same Shape Is Used Here Rather Than A Stack Trace
            if (exception is not null)
                message = $"{message} :: {exception.GetType().Name} :: {exception.Message}";

            logger.Log(category, message);
        }
    }
}
```

Create `source/COMPEL/Utilities/SynchronousProgress.cs`:

```csharp
namespace COMPEL.Utilities;

/// <summary>
///     An <see cref="IProgress{T}"/> that invokes its handler on the reporting thread.
///     <see cref="Progress{T}"/> posts its callbacks to the thread pool when there is no synchronisation context, which lets a stale report interleave with the lines written after the operation completes; reporting synchronously keeps the console and the log in order.
/// </summary>
internal sealed class SynchronousProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => handler(value);
}
```

- [x] **Step 6: Build and test**

Run: `dotnet build source/COMPEL.slnx && dotnet test source/COMPEL.slnx`
Expected: build succeeds with 0 warnings; 61 tests pass.

---

### Task 2: Wire The Logger Into Start-Up And The Host, Remove Serilog And The Banner

**Files:**
- Delete: `source/COMPEL/Banner.cs`
- Delete: `source/COMPEL.Tests/BannerTests.cs`
- Modify: `source/COMPEL/COMPEL.cs` (full replacement below)
- Modify: `source/COMPEL/Services/UpdateGate.cs`
- Modify: `source/COMPEL/COMPEL.csproj`

**Interfaces:**
- Consumes: `Logger`, `LogCategory`, `LoggerProvider`, `SynchronousProgress<T>` from Task 1.
- Produces: `UpdateGate.CheckForUpdates(Logger logger, SingleInstanceGuard singleInstanceGuard)`. The Serilog packages are gone, so no file may reference the `Serilog` namespace afterwards.

- [x] **Step 1: Delete the banner and its tests**

```bash
git rm source/COMPEL/Banner.cs source/COMPEL.Tests/BannerTests.cs
```

- [x] **Step 2: Remove the Serilog packages**

In `source/COMPEL/COMPEL.csproj`, delete the whole item group holding the three `PackageReference` elements for `Serilog.AspNetCore`, `Serilog.Sinks.Console`, and `Serilog.Sinks.File`, together with the blank line before it.

- [x] **Step 3: Replace the entry point**

Replace the full content of `source/COMPEL/COMPEL.cs` with:

```csharp
// Configuration Is A Single Self-Describing "COMPEL.json" File Beside The Executable; On First Run It Is Created With Defaults And The Process Stops So The Operator Can Configure It
// This Path Runs Before The Logger Exists Because The Release Workflow Runs The Published Binary Once To Generate The Shipped Configuration File And Fails If Anything Else Is Left Behind
if (CompelConfigurationLoader.Exists() is false)
{
    CompelConfigurationLoader.CreateDefault();

    Console.WriteLine($@"Created A Default Configuration File At ""{CompelConfigurationLoader.ResolvePath()}""; Set At Least ""UserName"" And ""Password"", Then Start COMPEL Again");

    return;
}

// Every Message From Here On Goes Through The Logger So The Console And "COMPEL.log" Carry The Same Session, Opened By A Session Header As In WILLOWMAKER
string logFilePath = Path.Combine(AppContext.BaseDirectory, DeploymentManifest.LogFileName);

Logger logger;

try
{
    logger = new Logger(logFilePath);
}

catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
{
    Console.WriteLine($@"COMPEL Could Not Open Its Log File ""{logFilePath}"": {exception.Message}");

    return;
}

CompelConfigurationFile configuration;

try
{
    configuration = CompelConfigurationLoader.Load();
}

catch (InvalidOperationException exception)
{
    logger.Log(LogCategory.Initialise, exception.Message);
    logger.Log(LogCategory.Initialise, $@"Fix Or Delete ""{CompelConfigurationLoader.ResolvePath()}"" And Start COMPEL Again");

    return;
}

// COMPEL Mirrors The Match Server Distribution Into Its Installation Directory, So It Refuses To Start From A Directory Whose Contents Are Neither An Existing Installation Nor A Fresh Deployment; The Synchronisation's Deletion Pass Would Otherwise Remove Unrelated Files
LocationGuard.Result locationSafety = LocationGuard.AssessLocationSafety(DistributionSynchronisationService.ResolveInstallationDirectory(new CDNOptions().InstallationDirectory));

logger.Log(LogCategory.Guard, locationSafety.Reason);

if (locationSafety.Verdict is LocationSafetyVerdict.Unsafe)
{
    foreach (string foreignEntry in LocationGuard.ApplyForeignEntriesDisplayCap(locationSafety.ForeignEntries))
        logger.Log(LogCategory.Guard, foreignEntry);

    logger.Log(LogCategory.Guard, "COMPEL Will Not Start From This Directory Because It Contains The Unrelated Entries Listed Above, Which The Distribution Synchronisation Would Delete");
    logger.Log(LogCategory.Guard, "Move COMPEL To An Empty Directory Or To An Existing Match Server Installation, Then Start It Again");

    return;
}

// The Control Plane Port Is Validated Here, Ahead Of Kestrel, So An Out-Of-Range Value Produces A Clear Message Rather Than An Unhandled Bind Failure Or A Silent Bind To An Arbitrary Ephemeral Port
if (configuration.ControlPlanePort.Value is < 1 or > 65535)
{
    logger.Log(LogCategory.Initialise, $"The Configured Control Plane Port ({configuration.ControlPlanePort.Value}) Is Invalid; It Must Be Between 1 And 65535");
    logger.Log(LogCategory.Initialise, $@"Fix ""{CompelConfigurationLoader.ResolvePath()}"" And Start COMPEL Again");

    return;
}

// COMPEL Requires Elevated Privileges On Both Platforms; The Manager Assigns Processor Affinity And Priority To Its Child Servers, Which Is Not Possible Otherwise, So There Is No Point Starting Without Them
if (Environment.IsPrivilegedProcess is false)
{
    logger.Log(LogCategory.Initialise, "COMPEL Requires Elevated Privileges To Run; The Match Server Manager Assigns Processor Affinity And Priority To Its Servers");
    logger.Log(LogCategory.Initialise, OperatingSystem.IsWindows() ? "Start COMPEL As An Administrator" : @"Start COMPEL As Root (For Example Via ""sudo"")");

    return;
}

// The Master Server Must Be Reachable Before Launching; The Servers Cannot Register Or Authenticate Without It, So An Unreachable Master Server Is A Hard Startup Failure
// A Localhost Gateway Uses A Loopback Master Server And Is Not Pinged
if (await MasterServerIsReachable(configuration.Gateway.Value, logger) is false)
{
    logger.Log(LogCategory.Initialise, @"The Master Server ""api.kongor.net"" Is Not Reachable; COMPEL Will Not Start");
    logger.Log(LogCategory.Initialise, "Check The Host's Network Connection, Then Start COMPEL Again");

    return;
}

// Refuse To Start If Another COMPEL Instance Is Already Running Against This Installation
string lockFilePath = Path.Combine(AppContext.BaseDirectory, DeploymentManifest.LockFileName);

if (SingleInstanceGuard.TryAcquire(lockFilePath, out SingleInstanceGuard singleInstanceGuard) is false)
{
    logger.Log(LogCategory.Guard, "Another COMPEL Instance Is Already Running Against This Installation");

    return;
}

// Check For A Newer COMPEL Release And Offer To Self-Update Before Any Services Start
// This Runs After The Single-Instance Lock So No Other COMPEL Process Holds The Files The Update Script Replaces; An Accepted Update Exits Into The Update Script And Never Returns
await UpdateGate.CheckForUpdates(logger, singleInstanceGuard);

// "CreateSlimBuilder" Initialises The Host With Only The Features Native AOT Needs, Keeping The Published Binary Small
WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(args);

builder.WebHost.UseUrls($"http://0.0.0.0:{configuration.ControlPlanePort.Value}");

// Options: The Host-Facing Settings Come From "COMPEL.json" (Validated At Startup); The Infrastructure Settings Use Built-In Defaults
builder.Services.AddSingleton<IValidateOptions<MatchServerManagerOptions>, MatchServerManagerOptionsValidator>();

builder.Services.AddOptions<MatchServerManagerOptions>().Configure(options =>
{
    options.UserName             = configuration.UserName.Value;
    options.Password             = configuration.Password.Value;
    options.Instances            = configuration.Instances.Value;
    options.WarmInstancesTarget  = configuration.WarmInstancesTarget.Value;
    options.Gateway              = configuration.Gateway.Value;
    options.Location             = configuration.Location.Value;
    options.ServerNamePrefix     = configuration.ServerNamePrefix.Value;
    options.UseProxy             = configuration.UseProxy.Value;
    options.PortRangeOffset      = configuration.PortRangeOffset.Value;
    options.RuntimeArtefactsPath = configuration.RuntimeArtefactsPath.Value;
}).ValidateOnStart();

builder.Services.AddOptions<ControlPlaneOptions>().Configure(options => options.AuthenticationToken = configuration.AuthenticationToken.Value);

builder.Services.AddOptions<CDNOptions>().Configure(options => options.Synchronisation = configuration.CDNSynchronisation.Value);

// JSON: Source-Generated Serialisation Metadata For The Minimal-API Responses (Required Under Native AOT)
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.TypeInfoResolverChain.Insert(0, ControlPlaneJSONContext.Default));

// Logging: The Hosted Services Log Through The Same Logger As The Start-Up Messages, So The Console And The Log File Read As One Stream In WILLOWMAKER's Format
// Level Filtering Stays With The Host; Framework Chatter Is Limited To Warnings Except For The Lifetime Messages That Mark The Host Starting And Stopping
builder.Logging.ClearProviders();
builder.Logging.AddProvider(new LoggerProvider(logger));
builder.Logging.SetMinimumLevel(LogLevel.Information);
builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Information);

// The Port Plan Is A Pure Function Of The Options, So It Is Registered Once As The Single Source Of Truth Shared By The Supervisor, The Proxy, And The Ping Responder
builder.Services.AddSingleton(serviceProvider => new PortPlan(serviceProvider.GetRequiredService<IOptions<MatchServerManagerOptions>>().Value));
builder.Services.AddSingleton<AddressResolver>();
builder.Services.AddSingleton<ArtefactsLocator>();
builder.Services.AddSingleton<DistributionSynchronisationService>();
builder.Services.AddSingleton<MatchServerManagerSupervisor>();
builder.Services.AddSingleton<UDPProxyService>();
builder.Services.AddSingleton<UDPPingResponder>();

// Hosted Services; The Stateful Singletons Are Re-Registered As Hosted Services So The Control Plane And The Health Checks Can Resolve Them To Read Their Live State
builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<DistributionSynchronisationService>());
builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<MatchServerManagerSupervisor>());
builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<UDPProxyService>());
builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<UDPPingResponder>());
builder.Services.AddHostedService<MaintenanceService>();

// Health Checks; The Ping Responder And Proxy Checks Surface A Port-Bind Failure Via "/health" Instead Of It Being Visible Only In A Log Line
builder.Services.AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy(), [ "live" ])
    .AddCheck<UDPPingResponderHealthCheck>("ping-responder")
    .AddCheck<UDPProxyServiceHealthCheck>("proxy");

// The Options Validation Failure Is Caught Here And Reported As A Clean Message, Matching How The Configuration-Load Path Above Reports Problems Rather Than Surfacing As A Host Startup Crash
try
{
    WebApplication application = builder.Build();

    // Trigger The Configured Options Validation Before The Host Starts, So A Configuration Problem Is Reported By The Handler Below Instead Of Being Logged As A Host Startup Failure With A Stack Trace
    _ = application.Services.GetRequiredService<IOptions<MatchServerManagerOptions>>().Value;

    // Released Explicitly On A Graceful Shutdown; A Crash Or A Forced Kill Still Releases The Underlying File Handle At The Operating-System Level
    application.Lifetime.ApplicationStopping.Register(() => singleInstanceGuard.Dispose());

    application.MapHealthChecks("/health");
    application.MapHealthChecks("/alive", new HealthCheckOptions { Predicate = registration => registration.Tags.Contains("live") });

    application.MapControlPlaneEndpoints();

    application.Run();
}

catch (IOException exception)
{
    singleInstanceGuard.Dispose();

    // Kestrel Surfaces A Failure To Bind The Control-Plane Port (For Example When It Is Already In Use) As An "IOException" During Startup; It Is Reported Cleanly Here Rather Than As An Unhandled Stack Trace
    logger.Log(LogCategory.Control, $"COMPEL Could Not Start Its HTTP Control Plane On Port {configuration.ControlPlanePort.Value}: {exception.Message}");
    logger.Log(LogCategory.Control, @"Ensure The Port Is Free Or Change ""ControlPlanePort"", Then Start COMPEL Again");

    return;
}

catch (OptionsValidationException exception)
{
    singleInstanceGuard.Dispose();

    logger.Log(LogCategory.Initialise, "The COMPEL Configuration Is Invalid:");

    foreach (string failure in exception.Failures)
        logger.Log(LogCategory.Initialise, $"    - {failure}");

    logger.Log(LogCategory.Initialise, $@"Fix ""{CompelConfigurationLoader.ResolvePath()}"" And Start COMPEL Again");

    return;
}

// Pings The Master Server (Unless The Gateway Is Loopback, Whose Master Server Is Local And Assumed Reachable), Reporting The Round-Trip Time On Success; A Filtered ICMP Response Is Treated As Unreachable, As In The Legacy Startup Check
static async Task<bool> MasterServerIsReachable(string gateway, Logger logger)
{
    if (gateway.Equals("localhost", StringComparison.OrdinalIgnoreCase) || gateway.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase))
        return true;

    const string masterServerHost = "api.kongor.net";

    try
    {
        using Ping ping = new ();

        PingReply reply = await ping.SendPingAsync(masterServerHost, TimeSpan.FromSeconds(5));

        if (reply.Status is not IPStatus.Success)
            return false;

        logger.Log(LogCategory.Initialise, $@"Master Server ""{masterServerHost}"" Is Reachable ({reply.RoundtripTime} ms)");

        return true;
    }

    catch
    {
        return false;
    }
}
```

- [x] **Step 4: Route the update gate's messages through the logger**

In `source/COMPEL/Services/UpdateGate.cs`:

Change the signature to `public static async Task CheckForUpdates(Logger logger, SingleInstanceGuard singleInstanceGuard)`.

Replace each `Console.WriteLine` listed on the left with the call on the right. The countdown prompt, the `\r` download percentage, the `Console.WriteLine()` after a key press, and the final `\rDownloading ...: 100%` line stay as they are.

| Current | Replacement |
| --- | --- |
| `Console.WriteLine($"Current Version: {VersionChecker.CurrentVersionDisplay}");` | `logger.Log(LogCategory.Version, $"Current Version: {VersionChecker.CurrentVersionDisplay}");` |
| `Console.WriteLine("Checking For Updates ...");` | `logger.Log(LogCategory.Version, "Checking For Updates ...");` |
| `Console.WriteLine($"The COMPEL Releases Repository Is Not Reachable: HTTP {statusCode}");` | `logger.Log(LogCategory.Version, $"The COMPEL Releases Repository Is Not Reachable: HTTP {statusCode}");` |
| `Console.WriteLine($"The COMPEL Releases Repository Is Not Reachable: {exception.GetType().Name}");` | `logger.Log(LogCategory.Version, $"The COMPEL Releases Repository Is Not Reachable: {exception.GetType().Name}");` |
| `Console.WriteLine("COMPEL Is Up To Date");` | `logger.Log(LogCategory.Version, "COMPEL Is Up To Date");` |
| `Console.WriteLine($"Update Available: {latestVersionDisplay}");` | `logger.Log(LogCategory.Version, $"Update Available: {latestVersionDisplay}");` |
| `Console.WriteLine("Update Skipped By User");` | `logger.Log(LogCategory.Update, "Update Skipped By User");` |
| `Console.WriteLine("Update Accepted By User");` | `logger.Log(LogCategory.Update, "Update Accepted By User");` |
| `Console.WriteLine($"No Downloadable Asset Found; Get The Update From {result.ReleasePageURL}");` | `logger.Log(LogCategory.Update, $"No Downloadable Asset Found; Get The Update From {result.ReleasePageURL}");` |
| `Console.WriteLine($"Downloading Update From {result.DownloadURL} ...");` | `logger.Log(LogCategory.Update, $"Downloading Update From {result.DownloadURL} ...");` |
| `Console.WriteLine("Restarting Into The Update Script ...");` | `logger.Log(LogCategory.Update, "Restarting Into The Update Script ...");` |
| `Console.WriteLine($"Update Failed: {exception.Message}");` | `logger.Log(LogCategory.Update, $"Update Failed: {exception.Message}");` |

Replace the silent non-interactive return:

```csharp
        if (Console.IsInputRedirected)
            return;
```

with:

```csharp
        if (Console.IsInputRedirected)
        {
            logger.Log(LogCategory.Update, "Update Skipped (Non-Interactive Console)");

            return;
        }
```

Delete the private nested class `SynchronousProgress<T>` at the bottom of the file together with the comment above it; the shared one from Task 1 is used instead and needs no code change at the call site.

- [x] **Step 5: Confirm Serilog is gone**

Run: `grep -rn 'Serilog' source --include=*.cs --include=*.csproj | grep -v '/obj/'`
Expected: no output.

- [x] **Step 6: Build and test**

Run: `dotnet build source/COMPEL.slnx && dotnet test source/COMPEL.slnx`
Expected: build succeeds with 0 warnings; 59 tests pass.

- [x] PARTIAL, run unelevated by the controller: **Step 7: Manual check**

Run `dotnet run --project source/COMPEL` from an elevated shell against a configured `COMPEL.json`. Expected on the console and in `COMPEL.log`: a `[GUARD______]` line with the guard verdict, `[VERSION____] Current Version: ...`, `[VERSION____] Checking For Updates ...`, then `[INITIALISE_]` lines from the hosting lifetime. Stop with Ctrl+C. Open `COMPEL.log` and confirm it begins with the session header on its first line and that the header is not preceded by a blank line.

---

### Task 3: Move The Deployment Types Into Services/Deployment

**Files:**
- Move: `source/COMPEL/SingleInstanceGuard.cs` to `source/COMPEL/Services/Deployment/SingleInstanceGuard.cs`
- Move: `source/COMPEL/Services/DeploymentManifest.cs` to `source/COMPEL/Services/Deployment/DeploymentManifest.cs`
- Move: `source/COMPEL/Services/LocationGuard.cs` to `source/COMPEL/Services/Deployment/LocationGuard.cs`
- Move: `source/COMPEL/Services/LocationSafetyVerdict.cs` to `source/COMPEL/Services/Deployment/LocationSafetyVerdict.cs`
- Modify: `source/COMPEL/Internals/UsingDirectives.cs`
- Modify: `source/COMPEL.Tests/GlobalUsings.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: namespace `COMPEL.Services.Deployment` containing `DeploymentManifest`, `LocationGuard`, `LocationSafetyVerdict`, and `SingleInstanceGuard`, all with unchanged members. The namespace `COMPEL.Services` still exists until Task 4 moves its remaining files.

- [x] **Step 1: Move the files**

```bash
mkdir -p source/COMPEL/Services/Deployment
git mv source/COMPEL/SingleInstanceGuard.cs source/COMPEL/Services/Deployment/SingleInstanceGuard.cs
git mv source/COMPEL/Services/DeploymentManifest.cs source/COMPEL/Services/Deployment/DeploymentManifest.cs
git mv source/COMPEL/Services/LocationGuard.cs source/COMPEL/Services/Deployment/LocationGuard.cs
git mv source/COMPEL/Services/LocationSafetyVerdict.cs source/COMPEL/Services/Deployment/LocationSafetyVerdict.cs
```

- [x] **Step 2: Update the namespace declarations**

```bash
sed -i 's/^namespace COMPEL;/namespace COMPEL.Services.Deployment;/' source/COMPEL/Services/Deployment/SingleInstanceGuard.cs
sed -i 's/^namespace COMPEL.Services;/namespace COMPEL.Services.Deployment;/' source/COMPEL/Services/Deployment/DeploymentManifest.cs source/COMPEL/Services/Deployment/LocationGuard.cs source/COMPEL/Services/Deployment/LocationSafetyVerdict.cs
head -1 source/COMPEL/Services/Deployment/*.cs
```

Expected: every file begins with `namespace COMPEL.Services.Deployment;`.

- [x] **Step 3: Add the namespace to both global-using files**

In `source/COMPEL/Internals/UsingDirectives.cs` and in `source/COMPEL.Tests/GlobalUsings.cs`, insert after `global using COMPEL.Services.ContentBroker;`:

```csharp
global using COMPEL.Services.Deployment;
```

- [x] **Step 4: Build and test**

Run: `dotnet build source/COMPEL.slnx && dotnet test source/COMPEL.slnx`
Expected: build succeeds with 0 warnings; 59 tests pass.

---

### Task 4: Move The Self-Update Types Into Services/Updates

**Files:**
- Move: `source/COMPEL/Services/UpdateGate.cs` to `source/COMPEL/Services/Updates/UpdateGate.cs`
- Move: `source/COMPEL/Services/VersionChecker.cs` to `source/COMPEL/Services/Updates/VersionChecker.cs`
- Move: `source/COMPEL/Services/VersionCheckResult.cs` to `source/COMPEL/Services/Updates/VersionCheckResult.cs`
- Modify: `source/COMPEL/Internals/UsingDirectives.cs`
- Modify: `source/COMPEL.Tests/GlobalUsings.cs`

**Interfaces:**
- Consumes: Task 3 layout.
- Produces: namespace `COMPEL.Services.Updates` containing `UpdateGate`, `VersionChecker`, and `VersionCheckResult`. The namespace `COMPEL.Services` no longer declares any type; `source/COMPEL/Services/` contains only folders.

- [x] **Step 1: Move the files**

```bash
mkdir -p source/COMPEL/Services/Updates
git mv source/COMPEL/Services/UpdateGate.cs source/COMPEL/Services/Updates/UpdateGate.cs
git mv source/COMPEL/Services/VersionChecker.cs source/COMPEL/Services/Updates/VersionChecker.cs
git mv source/COMPEL/Services/VersionCheckResult.cs source/COMPEL/Services/Updates/VersionCheckResult.cs
```

- [x] **Step 2: Update the namespace declarations**

```bash
sed -i 's/^namespace COMPEL.Services;/namespace COMPEL.Services.Updates;/' source/COMPEL/Services/Updates/UpdateGate.cs source/COMPEL/Services/Updates/VersionChecker.cs source/COMPEL/Services/Updates/VersionCheckResult.cs
head -1 source/COMPEL/Services/Updates/*.cs
```

Expected: every file begins with `namespace COMPEL.Services.Updates;`.

- [x] **Step 3: Replace the parent namespace in both global-using files**

In `source/COMPEL/Internals/UsingDirectives.cs` and in `source/COMPEL.Tests/GlobalUsings.cs`, remove `global using COMPEL.Services;` and insert after `global using COMPEL.Services.Supervision;`:

```csharp
global using COMPEL.Services.Updates;
```

- [x] **Step 4: Confirm the services folder holds only folders**

Run: `find source/COMPEL/Services -maxdepth 1 -type f`
Expected: no output.

- [x] **Step 5: Build and test**

Run: `dotnet build source/COMPEL.slnx && dotnet test source/COMPEL.slnx`
Expected: build succeeds with 0 warnings; 59 tests pass.

---

### Task 5: Structure The Tests Into Directories

**Files:**
- Move: `source/COMPEL.Tests/GlobalUsings.cs` to `source/COMPEL.Tests/Internals/UsingDirectives.cs`
- Move: `source/COMPEL.Tests/CompelConfigurationLoaderTests.cs` and `MatchServerManagerOptionsValidatorTests.cs` to `source/COMPEL.Tests/Configuration/`
- Move: `source/COMPEL.Tests/LocationGuardTests.cs` to `source/COMPEL.Tests/Services/Deployment/`
- Move: `source/COMPEL.Tests/UDPPingResponderTests.cs` to `source/COMPEL.Tests/Services/Ping/`
- Move: `source/COMPEL.Tests/UDPForwarderTests.cs` to `source/COMPEL.Tests/Services/Proxy/`
- Move: `source/COMPEL.Tests/AddressResolverTests.cs`, `ArtefactsLocatorTests.cs`, `ManagerArgumentsTests.cs`, `PortPlanTests.cs` to `source/COMPEL.Tests/Services/Supervision/`
- Move: `source/COMPEL.Tests/ContentBrokerTests.cs` to `source/COMPEL.Tests/Services/Synchronisation/`
- Move: `source/COMPEL.Tests/VersionCheckerTests.cs` to `source/COMPEL.Tests/Services/Updates/`

The `Constants/` and `Utilities/` test folders already exist from Task 1 with their final namespaces.

**Interfaces:**
- Consumes: Task 4 namespaces.
- Produces: test namespaces `COMPEL.Tests.Configuration`, `COMPEL.Tests.Services.Deployment`, `COMPEL.Tests.Services.Ping`, `COMPEL.Tests.Services.Proxy`, `COMPEL.Tests.Services.Supervision`, `COMPEL.Tests.Services.Synchronisation`, and `COMPEL.Tests.Services.Updates`. Later tasks add tests to files at these paths.

- [x] **Step 1: Move the files**

```bash
cd source/COMPEL.Tests
mkdir -p Internals Configuration Services/Deployment Services/Ping Services/Proxy Services/Supervision Services/Synchronisation Services/Updates
git mv GlobalUsings.cs Internals/UsingDirectives.cs
git mv CompelConfigurationLoaderTests.cs MatchServerManagerOptionsValidatorTests.cs Configuration/
git mv LocationGuardTests.cs Services/Deployment/
git mv UDPPingResponderTests.cs Services/Ping/
git mv UDPForwarderTests.cs Services/Proxy/
git mv AddressResolverTests.cs ArtefactsLocatorTests.cs ManagerArgumentsTests.cs PortPlanTests.cs Services/Supervision/
git mv ContentBrokerTests.cs Services/Synchronisation/
git mv VersionCheckerTests.cs Services/Updates/
cd ../..
```

- [x] **Step 2: Update every moved file's namespace to match its folder**

```bash
cd source/COMPEL.Tests
sed -i 's/^namespace COMPEL.Tests;/namespace COMPEL.Tests.Configuration;/' Configuration/*.cs
sed -i 's/^namespace COMPEL.Tests;/namespace COMPEL.Tests.Services.Deployment;/' Services/Deployment/*.cs
sed -i 's/^namespace COMPEL.Tests;/namespace COMPEL.Tests.Services.Ping;/' Services/Ping/*.cs
sed -i 's/^namespace COMPEL.Tests;/namespace COMPEL.Tests.Services.Proxy;/' Services/Proxy/*.cs
sed -i 's/^namespace COMPEL.Tests;/namespace COMPEL.Tests.Services.Supervision;/' Services/Supervision/*.cs
sed -i 's/^namespace COMPEL.Tests;/namespace COMPEL.Tests.Services.Synchronisation;/' Services/Synchronisation/*.cs
sed -i 's/^namespace COMPEL.Tests;/namespace COMPEL.Tests.Services.Updates;/' Services/Updates/*.cs
grep -rn '^namespace' --include=*.cs . | grep -v '/obj/' | grep -v '/bin/'
cd ../..
```

Expected: each file's namespace equals `COMPEL.Tests` plus its folder path with dots.

- [x] **Step 3: Rewrite the test global-using file**

Replace the full content of `source/COMPEL.Tests/Internals/UsingDirectives.cs` with the following. The `COMPEL` and `COMPEL.Endpoints` directives are dropped because no test references a type from either namespace after Task 2.

```csharp
global using COMPEL.Configuration;
global using COMPEL.Constants;
global using COMPEL.Services.ContentBroker;
global using COMPEL.Services.Deployment;
global using COMPEL.Services.Maintenance;
global using COMPEL.Services.Ping;
global using COMPEL.Services.Proxy;
global using COMPEL.Services.Supervision;
global using COMPEL.Services.Updates;
global using COMPEL.Utilities;

global using Microsoft.Extensions.Logging;
global using Microsoft.Extensions.Logging.Abstractions;
global using Microsoft.Extensions.Options;

global using System.Buffers.Binary;
global using System.Net;
global using System.Net.Sockets;
global using System.Reflection;
global using System.Security.Cryptography;
global using System.Text;
global using System.Text.RegularExpressions;
```

- [x] **Step 4: Confirm the test project root holds only the project file**

Run: `find source/COMPEL.Tests -maxdepth 1 -type f`
Expected: `source/COMPEL.Tests/COMPEL.Tests.csproj` only.

- [x] **Step 5: Build and test**

Run: `dotnet build source/COMPEL.slnx && dotnet test source/COMPEL.slnx`
Expected: build succeeds with 0 warnings; 59 tests pass.

---

### Task 6: Align Synchronisation Logging With WILLOWMAKER

**Files:**
- Modify: `source/COMPEL/Services/ContentBroker/DistributionSynchronisationService.cs`

**Interfaces:**
- Consumes: `SynchronousProgress<T>` from Task 1; `ContentBroker.FetchManifest` and `ContentBroker.Synchronise`, unchanged.
- Produces: no signature changes. `SynchroniseNow` now logs every failure itself, so its callers no longer need to.

Not covered by an automated test: the behaviour is the wording and ordering of log lines during a network operation. The build and the existing suite are the regression check; the manual check at the end shows the new lines.

- [x] **Step 1: Remove the throttled progress line and its state**

Delete the two fields `announcedTotalBytes` and `lastProgressLogTicks` and the whole `LogProgress` method together with the comment above it.

- [x] **Step 2: Replace the start-up loop**

Replace the whole `ExecuteAsync` method with:

```csharp
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The Initial Synchronisation Can Be Disabled For Development And Testing; The Existing Local Distribution Is Used, And On-Demand Synchronisation Via The Control Plane Still Works
        if (options.Synchronisation is false)
        {
            logger.LogInformation("SKIP: Synchronisation Skipped (Manual Override)");

            SynchronisationState = "Disabled";

            ready.TrySetResult();

            try { await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { }

            return;
        }

        // Mirroring The Launcher's Location Guard, A Development Environment Is Never Synchronised; The Mirror's Deletion Pass Would Otherwise Remove Development Artefacts That Are Not Part Of The Distribution
        if (LocationGuard.AssessLocationSafety(InstallationDirectory).Verdict is not LocationSafetyVerdict.Safe)
        {
            logger.LogInformation("SKIP: Synchronisation Skipped (Development Environment)");

            SynchronisationState = "Skipped (Development Environment)";

            ready.TrySetResult();

            try { await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { }

            return;
        }

        while (stoppingToken.IsCancellationRequested is false)
        {
            try
            {
                SynchronisationSummary summary = await SynchroniseNow(stoppingToken).ConfigureAwait(false);

                // A Synchronisation That Reports No Exception Can Still Have Failed To Fetch Individual Files, Which Would Leave A Mixed-Version Tree; Only Stop Retrying Once Every File Transferred And The Manager Executable Is Present
                if (summary.FilesFailed is 0 && File.Exists(ManagerExecutablePath))
                    break;

                logger.LogWarning("Retrying Synchronisation In {Seconds} Seconds", RetryDelay.TotalSeconds);
            }

            catch (OperationCanceledException)
            {
                return;
            }

            catch (Exception)
            {
                // The Failure Itself Was Logged By "SynchroniseNow"; If A Previous Synchronisation Already Installed The Manager, The Launch Proceeds With It Rather Than Blocking On A Transient CDN Outage
                if (File.Exists(ManagerExecutablePath))
                {
                    logger.LogWarning("Proceeding With The Existing Local Distribution At {InstallationDirectory}", InstallationDirectory);

                    ready.TrySetResult();

                    break;
                }

                logger.LogWarning("No Local Distribution Is Present; Retrying Synchronisation In {Seconds} Seconds", RetryDelay.TotalSeconds);
            }

            try { await Task.Delay(RetryDelay, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }

        // Remain Alive So The Control Plane Can Resolve This Singleton For On-Demand Synchronisations And Status Reporting
        try { await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }
```

- [x] **Step 3: Replace the synchronisation method**

Replace the whole `SynchroniseNow` method with:

```csharp
    /// <summary>
    ///     Fetches the manifest and synchronises the installation directory, logging each step in WILLOWMAKER's vocabulary. Safe to call concurrently; calls are serialised.
    ///     Every failure is logged here before it propagates, so the start-up loop and the control plane only decide what to do next.
    /// </summary>
    public async Task<SynchronisationSummary> SynchroniseNow(CancellationToken cancellationToken)
    {
        // Mirroring The Startup Path, A Location That Is Not Safe To Mirror Into (A Development Environment) Is Never Synchronised, Including On Demand Via The Control Plane
        if (LocationGuard.AssessLocationSafety(InstallationDirectory).Verdict is not LocationSafetyVerdict.Safe)
        {
            logger.LogInformation("SKIP: Synchronisation Skipped (Development Environment)");

            return new SynchronisationSummary(0, 0, 0, 0, 0, []);
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        synchronising = true;

        try
        {
            SynchronisationState = "Synchronising";

            logger.LogInformation(@"INIT: Fetching Manifest For Variant ""{Variant}"" From CDN", Variant);

            Manifest manifest = await ContentBroker.FetchManifest(Variant, options.Host, cancellationToken).ConfigureAwait(false);

            logger.LogInformation("INIT: Manifest Version {Version} Lists {Count} File(s)", manifest.Version, manifest.Files.Count);

            DistributionVersion = manifest.Version;

            // Reported Synchronously So The Per-File Lines Land In The Log In The Order The Broker Raises Them, With The Completion Line Last
            SynchronousProgress<SynchronisationEvent> progress = new (LogSynchronisationEvent);

            SynchronisationSummary summary = await ContentBroker.Synchronise(manifest, Variant, InstallationDirectory, options.Host, options.ParallelTransfers, progress, cancellationToken).ConfigureAwait(false);

            SynchronisationState = summary.FilesFailed is 0 ? "Up To Date" : $"Completed With {summary.FilesFailed} Failure(s)";

            if (summary.FilesFailed > 0)
                logger.LogWarning("FAIL: {Failures} File(s) Failed To Be Transferred", summary.FilesFailed);

            // Release Consumers Only Once Every File Transferred And The Manager Executable Is Present; A Synchronisation That Failed To Fetch Some Files Would Leave A Mixed-Version Tree, So The Manager Must Not Be Launched Against It
            if (summary.FilesFailed is 0 && File.Exists(ManagerExecutablePath))
                ready.TrySetResult();

            return summary;
        }

        catch (HttpRequestException exception)
        {
            string statusCode = exception.StatusCode is not null
                ? $"{(int) exception.StatusCode} ({exception.StatusCode})"
                : "Unknown Status Code";

            logger.LogError("FAIL: CDN Unreachable :: HTTP {StatusCode}", statusCode);

            SynchronisationState = "CDN Unreachable; Synchronisation Aborted";

            throw;
        }

        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError("FAIL: {ExceptionType} :: {Message}", exception.GetType().Name, exception.Message);

            SynchronisationState = $"Failed: {exception.Message}";

            throw;
        }

        finally
        {
            synchronising = false;

            gate.Release();
        }
    }
```

- [x] **Step 4: Replace the event logging**

Replace the whole `LogSynchronisationEvent` method with:

```csharp
    private void LogSynchronisationEvent(SynchronisationEvent synchronisationEvent)
    {
        switch (synchronisationEvent.Kind)
        {
            case SynchronisationEventKind.PlanReady:
                logger.LogInformation("PLAN: {Plan}", synchronisationEvent.Detail);
                break;

            case SynchronisationEventKind.Downloaded:
                logger.LogInformation("PULL: {Path}", synchronisationEvent.Detail);
                break;

            case SynchronisationEventKind.Deleted:
                logger.LogInformation("NUKE: {Path}", synchronisationEvent.Detail);
                break;

            case SynchronisationEventKind.Skipped:
                logger.LogInformation("SKIP: {Path}", synchronisationEvent.Detail);
                break;

            case SynchronisationEventKind.DownloadFailed:
            case SynchronisationEventKind.DeletionFailed:
                logger.LogWarning("FAIL: {Detail}", synchronisationEvent.Detail);
                break;

            case SynchronisationEventKind.Completed:
                logger.LogInformation("DONE: {Summary}", synchronisationEvent.Detail);
                break;
        }
    }
```

- [x] **Step 5: Build and test**

Run: `dotnet build source/COMPEL.slnx && dotnet test source/COMPEL.slnx`
Expected: build succeeds with 0 warnings; 59 tests pass.

- [ ] OWED, see "Owed Before Release": **Step 6: Manual check**

Run `dotnet run --project source/COMPEL -c Release` from an elevated shell in a directory holding only the build output and a configured `COMPEL.json`, so the location guard reports a baseline directory and a real synchronisation runs. Expected in order: `[SYNCHRONISE] INIT: Fetching Manifest For Variant "was" From CDN`, `INIT: Manifest Version ... Lists ... File(s)`, `PLAN: ...`, a `PULL:` line per file, and `DONE: ...` last. Stop with Ctrl+C.

---

### Task 7: Port The File Lock Detector And Log The Processes Holding Failed Files

**Files:**
- Create: `source/COMPEL/Utilities/FileLockDetector.cs` (copied from WILLOWMAKER)
- Modify: `source/COMPEL/Services/ContentBroker/DistributionSynchronisationService.cs`
- Test: `source/COMPEL.Tests/Utilities/FileLockDetectorTests.cs`

**Interfaces:**
- Consumes: `FileLockDetector.GetLockingProcesses(string filePath)` returning `List<FileLockingProcess>`, and the record `FileLockingProcess(int ProcessID, string ApplicationName)`, both from the ported file.
- Produces: after a synchronisation with failures, one `FAIL:` line per application holding a failed file, in place of WILLOWMAKER's dialog.

- [x] **Step 1: Write the failing test**

Create `source/COMPEL.Tests/Utilities/FileLockDetectorTests.cs`:

```csharp
namespace COMPEL.Tests.Utilities;

/// <summary>
///     Verifies the lock detector against the one process whose locks the test controls: itself.
/// </summary>
public sealed class FileLockDetectorTests
{
    [Test]
    public async Task The_Current_Process_Is_Reported_When_It_Holds_A_File_Open()
    {
        string path = Path.Combine(Path.GetTempPath(), $"compel-lock-{Guid.NewGuid():N}.bin");

        try
        {
            using (FileStream stream = new (path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                List<FileLockingProcess> lockingProcesses = FileLockDetector.GetLockingProcesses(path);

                await Assert.That(lockingProcesses.Select(lockingProcess => lockingProcess.ProcessID).Contains(Environment.ProcessId)).IsTrue();
            }
        }

        finally
        {
            File.Delete(path);
        }
    }
}
```

- [x] **Step 2: Run the build to verify the test fails**

Run: `dotnet build source/COMPEL.slnx`
Expected: compile error in the test project, `FileLockDetector` does not exist.

- [x] **Step 3: Copy the detector and change only its namespace**

```bash
cp "C:/Users/SADS-810/Source/WILLOWMAKER/source/WILLOWMAKER.Core/Utilities/FileLockDetector.cs" source/COMPEL/Utilities/FileLockDetector.cs
sed -i 's/^namespace WILLOWMAKER.Core.Utilities;/namespace COMPEL.Utilities;/' source/COMPEL/Utilities/FileLockDetector.cs
git diff --no-index "C:/Users/SADS-810/Source/WILLOWMAKER/source/WILLOWMAKER.Core/Utilities/FileLockDetector.cs" source/COMPEL/Utilities/FileLockDetector.cs
```

Expected: the diff shows the namespace line only.

- [x] **Step 4: Run the tests to verify the new one passes**

Run: `dotnet build source/COMPEL.slnx && dotnet test source/COMPEL.slnx`
Expected: build succeeds; 60 tests pass.

- [x] **Step 5: Log the locking processes after a failed synchronisation**

In `DistributionSynchronisationService.SynchroniseNow`, replace:

```csharp
            if (summary.FilesFailed > 0)
                logger.LogWarning("FAIL: {Failures} File(s) Failed To Be Transferred", summary.FilesFailed);
```

with:

```csharp
            if (summary.FilesFailed > 0)
            {
                logger.LogWarning("FAIL: {Failures} File(s) Failed To Be Transferred", summary.FilesFailed);

                LogLockingProcesses(summary.Failures);
            }
```

Add these members after `LogSynchronisationEvent`:

```csharp
    // The Launcher Shows The Processes Holding Failed Files In A Dialog Grouped By Application; The Console Equivalent Is One Line Per Application Naming The Files It Holds
    private void LogLockingProcesses(IReadOnlyList<SynchronisationFailure> failures)
    {
        const string unidentifiedProcessGroup = "Unidentified Process";

        Dictionary<string, LockGroup> groups = new (StringComparer.OrdinalIgnoreCase);

        LockGroup GroupFor(string applicationName)
        {
            if (groups.TryGetValue(applicationName, out LockGroup? group) is false)
            {
                group = new LockGroup();

                groups.Add(applicationName, group);
            }

            return group;
        }

        foreach (SynchronisationFailure failure in failures)
        {
            string absolutePath = Path.IsPathRooted(failure.Path)
                ? failure.Path
                : Path.GetFullPath(Path.Combine(InstallationDirectory, failure.Path));

            if (File.Exists(absolutePath) is false)
                continue;

            string displayPath = Path.GetRelativePath(InstallationDirectory, absolutePath);

            List<FileLockingProcess> lockingProcesses = FileLockDetector.GetLockingProcesses(absolutePath);

            if (lockingProcesses.Count is 0)
            {
                // No Locking Process Was Identified, So The File Is Only Surfaced When It Is Genuinely Still Locked; This Filters Out Failures Caused By Other Reasons (Such As A Hash Mismatch Or An Unreachable CDN) While Still Reporting A Lock Whose Owner Could Not Be Determined
                if (FileIsLocked(absolutePath))
                    GroupFor(unidentifiedProcessGroup).FilePaths.Add(displayPath);

                continue;
            }

            foreach (FileLockingProcess lockingProcess in lockingProcesses)
            {
                // Every Instance Of The Same Executable Is Collapsed Into One Group Keyed By Its Application Name; Its Distinct Process IDs Are Counted So The Operator Knows How Many Instances Need To Be Closed
                LockGroup group = GroupFor(lockingProcess.ApplicationName);

                group.ProcessIDs.Add(lockingProcess.ProcessID);
                group.FilePaths.Add(displayPath);
            }
        }

        foreach ((string applicationName, LockGroup group) in groups)
        {
            string processName = group.ProcessIDs.Count > 1
                ? $"{applicationName} ({group.ProcessIDs.Count} Processes)"
                : applicationName;

            logger.LogWarning("FAIL: {ProcessName} Holds {Files}", processName, string.Join(", ", group.FilePaths));
        }
    }

    private static bool FileIsLocked(string path)
    {
        try
        {
            // Opening With No Sharing Fails When Any Other Handle To The File Is Already Open, Which Is The Defining Symptom Of A Lock Held By Another Process; Read Access Is Requested So The Read-Only Attribute Does Not Interfere
            using FileStream stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None);

            return false;
        }

        catch (IOException)
        {
            return true;
        }

        catch
        {
            // Swallowed Deliberately: An Inability To Open The File For Reasons Other Than Sharing (Such As Insufficient Permissions) Must Not Be Misreported As A Lock
            return false;
        }
    }

    /// <summary>
    ///     Accumulates the distinct locking process IDs and the locked file paths for a single application while the locking processes are being scanned.
    /// </summary>
    private sealed class LockGroup
    {
        public HashSet<int> ProcessIDs { get; } = [];

        public HashSet<string> FilePaths { get; } = new (StringComparer.OrdinalIgnoreCase);
    }
```

- [x] **Step 6: Build and test**

Run: `dotnet build source/COMPEL.slnx && dotnet test source/COMPEL.slnx`
Expected: build succeeds with 0 warnings; 60 tests pass.

---

### Task 8: Resolve The Distribution Version When Synchronisation Is Skipped

**Files:**
- Modify: `source/COMPEL/Services/ContentBroker/DistributionSynchronisationService.cs` in `ExecuteAsync`, plus one new private method
- Modify: `source/COMPEL/Services/Ping/UDPPingResponder.cs` in `ExecuteAsync`

**Interfaces:**
- Consumes: `ContentBroker.FetchManifest` and the existing `DistributionVersion` property.
- Produces: `DistributionVersion` is populated from the manifest alone when `CDNSynchronisation` is disabled or the location guard skips synchronisation, and the ping responder logs a warning when it still has no version.

Not covered by an automated test: it requires the CDN. Manual check at the end of the task.

- [x] **Step 1: Add the manifest-only resolution to the service**

Add this method after `SynchroniseNow`:

```csharp
    /// <summary>
    ///     Fetches only the manifest so the distribution version is known when the files themselves are not synchronised.
    ///     The ping responder advertises that version, and clients discard any server whose version does not match their own, so an unknown version makes this host unselectable.
    /// </summary>
    private async Task ResolveDistributionVersionWithoutSynchronising(CancellationToken cancellationToken)
    {
        try
        {
            logger.LogInformation(@"INIT: Fetching Manifest For Variant ""{Variant}"" From CDN", Variant);

            Manifest manifest = await ContentBroker.FetchManifest(Variant, options.Host, cancellationToken).ConfigureAwait(false);

            logger.LogInformation("INIT: Manifest Version {Version} Lists {Count} File(s)", manifest.Version, manifest.Files.Count);

            DistributionVersion = manifest.Version;
        }

        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning("FAIL: {ExceptionType} :: {Message} :: Pongs Will Advertise No Version And Clients Will Not List This Server", exception.GetType().Name, exception.Message);
        }
    }
```

- [x] **Step 2: Call it from both skip branches**

In `ExecuteAsync`, both skip branches contain a `SKIP:` log line followed by a blank line and a `SynchronisationState = ...` assignment. In each branch, insert between the log line and the assignment:

```csharp
            try { await ResolveDistributionVersionWithoutSynchronising(stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
```

- [x] **Step 3: Warn from the ping responder when no version is known**

In `UDPPingResponder.ExecuteAsync`, after:

```csharp
        string? templateVersion = distribution.DistributionVersion;
        byte[] response = BuildResponseTemplate(options.ServerNamePrefix, templateVersion);
```

insert:

```csharp
        if (templateVersion is null)
            logger.LogWarning("No Distribution Version Is Known; Pongs Will Advertise An Empty Version And Clients Will Not List This Server");
```

- [x] **Step 4: Build and test**

Run: `dotnet build source/COMPEL.slnx && dotnet test source/COMPEL.slnx`
Expected: build succeeds with 0 warnings; 60 tests pass.

- [ ] OWED, see "Owed Before Release": **Step 5: Manual check**

Run `dotnet run --project source/COMPEL` from an elevated shell with `CDNSynchronisation` set to `false` in `COMPEL.json` and valid credentials. Expected on the console: `[SYNCHRONISE] SKIP: Synchronisation Skipped (Manual Override)`, then the two `INIT:` lines, and no `No Distribution Version Is Known` warning from the ping responder. Stop with Ctrl+C.

---

### Task 9: Fix The Supervisor's Orphan Sweep And Stale Process Reference

**Files:**
- Modify: `source/COMPEL/Services/Supervision/MatchServerManagerSupervisor.cs` in `KillOrphanedProcesses` and `LaunchProcess`

**Interfaces:**
- Consumes: nothing.
- Produces: no signature changes.

No automated test covers either change: the first needs a live Linux process table, and the second needs a `Process` that fails to start. Both were verified by reading the .NET runtime source and the supervisor's control flow; the build and the existing suite are the regression check.

- [x] **Step 1: Remove the fifteen-character truncation**

In `KillOrphanedProcesses`, replace this block:

```csharp
        string executableName = Path.GetFileNameWithoutExtension(HeroesOfNewerthExecutable.FileName);

        // Linux Exposes The Process Name Via "/proc/[pid]/comm", Which Is Truncated To 15 Characters, So The Lookup Name Is Truncated To Match; The Executable-Path Comparison Below Still Confirms The Process Identity.
        if (OperatingSystem.IsLinux() && executableName.Length > 15)
            executableName = executableName[..15];
```

with:

```csharp
        // On Linux ".NET" Resolves The Untruncated Process Name From The Command Line Rather Than From The Kernel's Truncated "comm" Field, So The Full Executable Name Is The Correct Lookup Key On Both Platforms; The Executable-Path Comparison Below Confirms The Process Identity
        string executableName = Path.GetFileNameWithoutExtension(HeroesOfNewerthExecutable.FileName);
```

- [x] **Step 2: Clear the field after disposing the previous process**

In `LaunchProcess`, replace this block:

```csharp
        // Dispose The Previous Exited Process Object Before Replacing It.
        Process? previous = managerProcess;

        if (previous is not null)
        {
            previous.Exited -= OnProcessExited;
            previous.Dispose();
        }
```

with:

```csharp
        // The Field Is Cleared As Soon As The Previous Process Is Disposed So A Launch That Throws Below Cannot Leave The Stop And Restart Paths Touching A Disposed Object
        Process? previous = Interlocked.Exchange(ref managerProcess, null);

        if (previous is not null)
        {
            previous.Exited -= OnProcessExited;
            previous.Dispose();
        }
```

- [x] **Step 3: Build and test**

Run: `dotnet build source/COMPEL.slnx && dotnet test source/COMPEL.slnx`
Expected: build succeeds with 0 warnings; 60 tests pass.

---

### Task 10: Reject Whitespace In Credentials

**Files:**
- Modify: `source/COMPEL/Configuration/MatchServerManagerOptionsValidator.cs`
- Test: `source/COMPEL.Tests/Configuration/MatchServerManagerOptionsValidatorTests.cs`

**Interfaces:**
- Consumes: `MatchServerManagerOptionsValidator.Validate(string? name, MatchServerManagerOptions options)`.
- Produces: validation failures for a `UserName` or `Password` containing any `char.IsWhiteSpace` character. `ServerNamePrefix` keeps accepting spaces.

- [x] **Step 1: Write the failing tests**

Add to `MatchServerManagerOptionsValidatorTests`, after `The_Default_Placeholder_Password_Is_Rejected`:

```csharp
    [Test]
    public async Task A_User_Name_Containing_Whitespace_Is_Rejected()
    {
        MatchServerManagerOptions options = ValidOptions();
        options.UserName = "KON GOR";

        await Assert.That(Validate(options).Failed).IsTrue();
    }

    [Test]
    public async Task A_Password_Containing_Whitespace_Is_Rejected()
    {
        MatchServerManagerOptions options = ValidOptions();
        options.Password = "open sesame";

        await Assert.That(Validate(options).Failed).IsTrue();
    }
```

- [x] **Step 2: Run the tests to verify they fail**

Run: `dotnet test source/COMPEL.slnx`
Expected: 62 total, 2 failed, both new whitespace tests.

- [x] **Step 3: Add the whitespace check to the validator**

Replace the two credential checks:

```csharp
        else if (ContainsUnsafeManagerArgumentCharacter(options.UserName))
            failures.Add(@"""UserName"" Must Not Contain A Double Quote, Semicolon, Or Control Character");
```

```csharp
        else if (ContainsUnsafeManagerArgumentCharacter(options.Password))
            failures.Add(@"""Password"" Must Not Contain A Double Quote, Semicolon, Or Control Character");
```

with:

```csharp
        else if (ContainsUnsafeManagerArgumentCharacter(options.UserName) || ContainsWhitespace(options.UserName))
            failures.Add(@"""UserName"" Must Not Contain Whitespace, A Double Quote, A Semicolon, Or A Control Character");
```

```csharp
        else if (ContainsUnsafeManagerArgumentCharacter(options.Password) || ContainsWhitespace(options.Password))
            failures.Add(@"""Password"" Must Not Contain Whitespace, A Double Quote, A Semicolon, Or A Control Character");
```

Add the helper after `ContainsUnsafeManagerArgumentCharacter`:

```csharp
    // The Manager's "Set" Command Splits Its Value On Whitespace And Drops The Final Token, Which Is Why The Server Name Carries Workaround Tokens; A Credential Cannot Carry Them, So Whitespace Is Rejected Outright Rather Than Being Silently Truncated
    private static bool ContainsWhitespace(string value) => value.Any(char.IsWhiteSpace);
```

- [x] **Step 4: Run the tests to verify they pass**

Run: `dotnet test source/COMPEL.slnx`
Expected: 62 tests pass, including `A_Fully_Configured_Set_Of_Options_Passes`, which proves the server name prefix `KONGOR ARENA` is still accepted.

---

### Task 11: Upper-Case The Location Passed To The Manager

**Files:**
- Modify: `source/COMPEL/Services/Supervision/ManagerArguments.cs` at the `svr_location` entry
- Test: `source/COMPEL.Tests/Services/Supervision/ManagerArgumentsTests.cs`

**Interfaces:**
- Consumes: `ManagerArguments.Build(MatchServerManagerOptions options, PortPlan ports, string serverAddress, string masterServerHostAndPort)`.
- Produces: the `svr_location` value in the `-execute` payload is always upper case.

- [x] **Step 1: Write the failing test**

Add to `ManagerArgumentsTests`, after `The_Execute_Payload_Carries_The_Legacy_Flags_And_The_New_Reauthentication_Frequency`:

```csharp
    [Test]
    public async Task The_Location_Is_Upper_Cased_In_The_Execute_Payload()
    {
        MatchServerManagerOptions options = SampleOptions();
        options.Location = "eu";

        string joined = string.Join(' ', ManagerArguments.Build(options, new PortPlan(options), "1.2.3.4", "api.kongor.net"));

        await Assert.That(joined.Contains("Set svr_location EU")).IsTrue();
    }
```

- [x] **Step 2: Run the test to verify it fails**

Run: `dotnet test source/COMPEL.slnx`
Expected: 63 total, 1 failed, `The_Location_Is_Upper_Cased_In_The_Execute_Payload`.

- [x] **Step 3: Normalise the value**

In `ManagerArguments.Build`, replace:

```csharp
            ["svr_location"]            = options.Location,
```

with:

```csharp
            // The Validator Accepts The Location In Any Case, But The Master Server Compares Regions Exactly
            ["svr_location"]            = options.Location.ToUpperInvariant(),
```

- [x] **Step 4: Run the tests to verify they pass**

Run: `dotnet test source/COMPEL.slnx`
Expected: 63 tests pass.

---

### Task 12: Escape Apostrophes In The Windows Update Script

**Files:**
- Modify: `source/COMPEL/Services/Updates/VersionChecker.cs` in `SpawnWindowsUpdateScript`
- Test: `source/COMPEL.Tests/Services/Updates/VersionCheckerTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces: `internal static string BuildWindowsUpdateScript(string archivePath, string sourceDirectory, string targetDirectory, string executablePath, string[] relativePathsToReplace)` returning the PowerShell script text with every embedded path escaped for a single-quoted literal. `SpawnWindowsUpdateScript` writes what this returns.

The Linux script needs no change: its paths sit inside double-quoted shell strings, where an apostrophe is literal, and the single-quoted word list contains only paths from the release archive, which COMPEL controls.

- [x] **Step 1: Write the failing test**

Add to `VersionCheckerTests`:

```csharp
    [Test]
    public async Task Apostrophes_In_Paths_Are_Doubled_In_The_Windows_Update_Script()
    {
        string script = VersionChecker.BuildWindowsUpdateScript
        (
            archivePath:            @"C:\Users\O'Brien\AppData\Local\Temp\COMPEL.update.zip",
            sourceDirectory:        @"C:\Users\O'Brien\AppData\Local\Temp\COMPEL.update",
            targetDirectory:        @"C:\Servers\COMPEL",
            executablePath:         @"C:\Servers\COMPEL\COMPEL.exe",
            relativePathsToReplace: [ "COMPEL.exe" ]
        );

        using (Assert.Multiple())
        {
            await Assert.That(script.Contains(@"'C:\Users\O''Brien\AppData\Local\Temp\COMPEL.update.zip'")).IsTrue();
            await Assert.That(script.Contains(@"'C:\Users\O''Brien\AppData\Local\Temp\COMPEL.update\*'")).IsTrue();
            await Assert.That(script.Contains(@"O'Brien")).IsFalse();
        }
    }
```

- [x] **Step 2: Run the build to verify the test fails**

Run: `dotnet build source/COMPEL.slnx`
Expected: compile error CS0117, `VersionChecker` does not contain a definition for `BuildWindowsUpdateScript`.

- [x] **Step 3: Extract the script builder and escape the literals**

Replace the whole `SpawnWindowsUpdateScript` method with:

```csharp
    private static void SpawnWindowsUpdateScript(string archivePath, string sourceDirectory, string targetDirectory, string executablePath, string[] relativePathsToReplace)
    {
        string scriptPath = Path.Combine(Path.GetTempPath(), "COMPEL.update.ps1");

        File.WriteAllText(scriptPath, BuildWindowsUpdateScript(archivePath, sourceDirectory, targetDirectory, executablePath, relativePathsToReplace));

        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell",
            Arguments = $@"-ExecutionPolicy Bypass -NoProfile -File ""{scriptPath}""",
            UseShellExecute = false,
            CreateNoWindow = true
        });
    }

    /// <summary>
    ///     Builds the PowerShell script that replaces the installed files with the extracted release and relaunches COMPEL.
    ///     Every path is embedded in a single-quoted literal, so apostrophes are doubled first; a user profile directory can legitimately contain one.
    /// </summary>
    internal static string BuildWindowsUpdateScript(string archivePath, string sourceDirectory, string targetDirectory, string executablePath, string[] relativePathsToReplace)
    {
        string escapedArchivePath     = EscapePowerShellLiteral(archivePath);
        string escapedSourceDirectory = EscapePowerShellLiteral(sourceDirectory);
        string escapedTargetDirectory = EscapePowerShellLiteral(targetDirectory);
        string escapedExecutablePath  = EscapePowerShellLiteral(executablePath);

        // The Relative Paths Are Embedded As A PowerShell Single-Quoted Array Literal So Each One Can Be Force-Deleted Before The New Files Are Copied In
        string pathArrayLiteral = string.Join(", ", relativePathsToReplace.Select(relativePath => $"'{EscapePowerShellLiteral(relativePath)}'"));

        return
        $$"""
            Start-Sleep -Milliseconds 3500
            $relativePathsToReplace = @({{pathArrayLiteral}})
            foreach ($relativePath in $relativePathsToReplace) {
                $stalePath = Join-Path '{{escapedTargetDirectory}}' $relativePath
                if (Test-Path -LiteralPath $stalePath) {
                    $staleItem = Get-Item -LiteralPath $stalePath -Force
                    if ($staleItem.Attributes -band [System.IO.FileAttributes]::ReadOnly) {
                        $staleItem.Attributes = $staleItem.Attributes -band -bnot [System.IO.FileAttributes]::ReadOnly
                    }
                    Remove-Item -LiteralPath $stalePath -Force
                }
            }
            Copy-Item -Path '{{escapedSourceDirectory}}\*' -Destination '{{escapedTargetDirectory}}' -Recurse -Force
            Start-Process -FilePath '{{escapedExecutablePath}}'
            Remove-Item -Path '{{escapedSourceDirectory}}' -Recurse -Force
            Remove-Item -Path '{{escapedArchivePath}}' -Force
            Remove-Item -Path $MyInvocation.MyCommand.Source -Force
        """;
    }

    // A PowerShell Single-Quoted Literal Represents An Embedded Apostrophe As Two Apostrophes
    private static string EscapePowerShellLiteral(string value) => value.Replace("'", "''");
```

- [x] **Step 4: Run the tests to verify they pass**

Run: `dotnet test source/COMPEL.slnx`
Expected: 64 tests pass.

---

### Task 13: Report Unreadable Configuration Files Cleanly

**Files:**
- Modify: `source/COMPEL/Configuration/CompelConfigurationLoader.cs` in `Load(string path)` and its `<exception>` documentation
- Test: `source/COMPEL.Tests/Configuration/CompelConfigurationLoaderTests.cs`

**Interfaces:**
- Consumes: `CompelConfigurationLoader.Load(string path)`.
- Produces: `Load` throws `InvalidOperationException` for an unreadable file as well as for invalid JSON, so the existing handler in `COMPEL.cs` reports both cleanly.

- [x] **Step 1: Write the failing test**

Add to `CompelConfigurationLoaderTests`:

```csharp
    [Test]
    public async Task An_Unreadable_File_Is_Reported_As_An_Invalid_Operation_Rather_Than_A_Raw_IO_Error()
    {
        string path = Path.Combine(Path.GetTempPath(), $"compel-configuration-{Guid.NewGuid():N}", "COMPEL.json");

        bool threw = false;

        try
        {
            CompelConfigurationLoader.Load(path);
        }

        catch (InvalidOperationException)
        {
            threw = true;
        }

        await Assert.That(threw).IsTrue();
    }
```

- [x] **Step 2: Run the test to verify it fails**

Run: `dotnet test source/COMPEL.slnx`
Expected: 65 total, 1 failed, `An_Unreadable_File_Is_Reported_As_An_Invalid_Operation_Rather_Than_A_Raw_IO_Error` surfacing `DirectoryNotFoundException`.

- [x] **Step 3: Wrap the file read**

In `Load(string path)`, replace:

```csharp
        string json = File.ReadAllText(path);
```

with:

```csharp
        string json;

        try
        {
            json = File.ReadAllText(path);
        }

        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($@"""COMPEL.json"" Could Not Be Read: {exception.Message}", exception);
        }
```

Replace the `<exception>` element on the public `Load()` with:

```csharp
    /// <exception cref="InvalidOperationException">
    ///     Thrown with a message naming the problem when "COMPEL.json" cannot be read, is not valid JSON, or does not match the expected shape (for example a string where a number is expected), rather than letting a raw <see cref="IOException"/> or <see cref="JsonException"/> propagate.
    /// </exception>
```

- [x] **Step 4: Run the tests to verify they pass**

Run: `dotnet test source/COMPEL.slnx`
Expected: 65 tests pass.

---

### Task 14: Align The Configuration Documentation

**Files:**
- Modify: `source/COMPEL/Configuration/CompelConfigurationFile.cs` in `GatewaySetting` and `LocationSetting`
- Modify: `source/COMPEL/Configuration/MatchServerManagerOptions.cs` in the class summary and the `Location` default

**Interfaces:**
- Consumes: nothing.
- Produces: descriptions that list every value the validator and the address resolver accept.

- [x] **Step 1: Update the two descriptions in the configuration file model**

Replace the `GatewaySetting.Description` value with:

```csharp
    public string Description => "The entry point for game servers and the server manager. Use 'kongor.net' for the official public gateway, 'localhost' for local development, 'PUBLIC' to auto-detect the public IP address, a LAN or public IP address, or a local or public host name to resolve.";
```

Replace the `LocationSetting.Description` value with:

```csharp
    public string Description => "Normally, the location can be set to any value, but, in order for the server to be TMM-compatible, only the following values are valid: 'USW', 'USE', 'EU', 'AU', 'BR', 'RU', 'SEA', and 'NEWERTH'.";
```

- [x] **Step 2: Align the options class**

In `MatchServerManagerOptions.cs`, change the summary line:

```csharp
///     These are the cross-platform equivalents of the keys that the legacy COMPEL stored in its "COMPEL.JSON" file.
```

to:

```csharp
///     These are the cross-platform equivalents of the keys that the legacy COMPEL stored in its "COMPEL.json" file.
```

and change the default:

```csharp
    public string Location { get; set; } = "NEWERTH";
```

to:

```csharp
    public string Location { get; set; } = "EU";
```

- [x] **Step 3: Build and test**

Run: `dotnet build source/COMPEL.slnx && dotnet test source/COMPEL.slnx`
Expected: build succeeds; 65 tests pass, including `A_Generated_Default_File_Loads_Back_With_Its_Default_Values`.

---

### Task 15: Strip Trailing Punctuation From Start Case Comments

**Files:**
- Modify: every `.cs` file under `source/COMPEL` and `source/COMPEL.Tests` that the detection command below lists. At the start of the plan that was 103 lines in 19 files; Tasks 2 and 6 already rewrote the comments in the entry point and the synchronisation service, so fewer remain. `ContentBroker.cs`, `FileLockDetector.cs`, `VersionChecker.cs`, `UpdateGate.cs`, and `LocationGuard.cs` already comply and must not be touched here.

**Interfaces:**
- Consumes: nothing.
- Produces: no code changes; comments only.

Rules to apply to every `//` line comment and every `/* */` block comment. XML documentation lines starting with `///` are out of scope and keep their full stops.

1. A single-sentence comment loses its trailing full stop.
2. A comment of two or more short sentences becomes one line with the sentences joined by semicolons, and no trailing punctuation.
3. A comment of two or more long sentences becomes one line per sentence, each line starting with `//` at the same indentation, and no trailing punctuation on any line.
4. `e.g.` and `E.G.` become `For Example` so the detection command stays clean.
5. Colons inside a sentence stay; parentheses stay.

Worked example for rule 3, `source/COMPEL/Endpoints/ControlPlaneEndpoints.cs` in the `/sync` handler:

```csharp
            // Synchronising Rewrites The Installation Directory The Manager Runs From
            // Doing So While The Manager Is Running Would Delete Or Overwrite Files The Live Servers Hold Open (A Sharing Violation On Windows, A Replaced Inode On Linux), So The Manager Must Be Stopped First
            // Checking The Desired State (Not Just The Live State) Also Rejects The Request When The Manager Has Merely Crashed And The Supervisor Is About To Relaunch It
```

Worked example for a block comment, same file:

```csharp
                catch { /* The Outcome Is Recorded On The Service's State And The Failure Is Logged Within */ }
```

Worked example for rules 2 and 4, `source/COMPEL/Services/Supervision/MatchServerManagerSupervisor.cs` above the reconcile loop:

```csharp
        // Event-Driven Reconcile Loop: Each Pass Brings The Process State Into Line With The Desired State, Then Waits For The Next Change (A Process Exit Or A Control-Plane Request)
        // Whenever The Manager Should Be Running But Isn't, The Wait Is Bounded By "RestartBackoff" So A Launch Failure (For Example A Missing Executable) Retries Automatically Instead Of Stalling Forever With No Signal To Wake It
```

- [x] **Step 1: List the offending lines**

```bash
grep -rnP --include=*.cs '^\s*//(?!/).*(\.\s*$|\.\s+[A-Z"(])' source/COMPEL source/COMPEL.Tests | grep -v '/obj/' | grep -v 'https\?://'
grep -rnP --include=*.cs '/\*.*\.\s*\*/' source/COMPEL source/COMPEL.Tests | grep -v '/obj/'
```

Expected: a list of comment lines from the first command and one line from the second.

- [x] **Step 2: Rewrite each listed comment by hand following the rules above**

Work file by file. Do not edit `source/COMPEL/Services/ContentBroker/ContentBroker.cs` or `source/COMPEL/Utilities/FileLockDetector.cs`; neither is on the list, and any change to them must be mirrored to WILLOWMAKER.

- [x] **Step 3: Re-run the detection commands**

Run the two commands from Step 1 again.
Expected: no output from either.

- [x] **Step 4: Confirm nothing but comments changed in this task**

Run: `git diff --stat` and inspect each hunk with `git diff`.
Expected: every changed line in this task's files is a comment line.

- [x] **Step 5: Build and test**

Run: `dotnet build source/COMPEL.slnx && dotnet test source/COMPEL.slnx`
Expected: build succeeds; 65 tests pass.

---

### Task 16: Fix Comment Content, Placement, And Casing Outliers

**Files:**
- Modify: `source/COMPEL/Services/Proxy/UDPProxyService.cs` at the TODO above the class
- Modify: `source/COMPEL/Services/Supervision/ManagerArguments.cs` at the `man_masterLogin` comment
- Modify: `source/COMPEL/Services/Proxy/UDPForwarder.cs` at the two constant comments near the top
- Modify: `source/COMPEL/Services/Supervision/MatchServerManagerSupervisor.cs` at the proxy-readiness comment in `ExecuteAsync`
- Modify: `source/COMPEL/Endpoints/ControlPlaneEndpoints.cs` at the `/ping` comment
- Modify: `source/COMPEL.Tests/Services/Proxy/UDPForwarderTests.cs` at the server socket comment
- Modify: `source/Directory.Build.props` at the comment inside the conditional property group

**Interfaces:**
- Consumes: Task 15 output, so the lines below already lack trailing full stops.
- Produces: no code changes; comments only.

- [x] **Step 1: Relocate and recase the proxy TODO**

In `UDPProxyService.cs`, delete the `// TODO:` line that sits between the `/// </summary>` line and `public sealed class UDPProxyService`. Insert these three lines immediately above the `/// <summary>` line:

```csharp
// TODO: This Proxy Performs The Transport, Port Remapping, And Client Authentication Only; It Does Not Detect Cheaters Or Ban Anyone
// The Native Proxy's Detection Heuristics Lived In A Closed Binary And Are Not Reproduced, And The Previous Firewall And Ban-List Mechanism Was Removed As Ineffective
// A Future Redesign Is Expected To Introduce A Different Enforcement Approach, Likely Not A Static Ban List, At Which Point A Hook To Drop Or Block Traffic Per Source Can Be Reintroduced
```

- [x] **Step 2: Quote the symbol references**

In `ManagerArguments.cs`, replace the comment above `["man_masterLogin"]` with:

```csharp
            // Append ":" So Game Server Instances Can Be Mapped To An Account Name, For Example "KONGOR:1" And "KONGOR:2"; The Manager Appends An Incremental Index To This Value
```

In `UDPForwarder.cs`, replace the comment above `WatermarkPrefixLength` with:

```csharp
    // The Challenge Packet's Leading Watermark Bytes, Which The Client Skips Before Reading The Control Payload: "WATERMARK_LEN_TOTAL" Plus "ENHANCED_WATERMARK_LEN_TOTAL"
```

and the comment above `ProxyPacketFlag` with:

```csharp
    // Identifies A Proxy Control Packet ("PACKET_PROXY", Bit 6) And The Challenge Sub-Type Within It
```

- [x] **Step 3: Remove the drift-prone offset from the supervisor comment**

In `MatchServerManagerSupervisor.ExecuteAsync`, the comment above `if (options.UseProxy)` starts with `// When The Proxy Is Enabled The Manager Advertises Public Ports (Local + 10000) That Only Work If The Proxy Bound Them`. Replace the parenthetical so the line reads:

```csharp
        // When The Proxy Is Enabled The Manager Advertises Public Ports (The Local Ports Raised By "PortPlan.ProxyPublicOffset") That Only Work If The Proxy Bound Them
```

Keep the following line of that comment as Task 15 left it.

- [x] **Step 4: Delete the comments that describe obvious code**

Delete these lines, each together with nothing else:

- `source/COMPEL/Endpoints/ControlPlaneEndpoints.cs`: `// Anonymous Latency Probe`
- `source/COMPEL.Tests/Services/Proxy/UDPForwarderTests.cs`: `// The Server The Forwarder Relays To`

The obvious comments in the entry point were dropped by the rewrite in Task 2, and the one in `LaunchProcess` was replaced in Task 9.

- [x] **Step 5: Recase the MSBuild comment**

In `source/Directory.Build.props`, replace the comment inside the conditional property group with:

```xml
        <!--
            Define The "DEVELOPMENT" Symbol For Every Configuration Except "Release", So The Code Can Distinguish A Development Build From A Release Build
        -->
```

- [x] **Step 6: Build and test**

Run: `dotnet build source/COMPEL.slnx && dotnet test source/COMPEL.slnx`
Expected: build succeeds; 65 tests pass.

---

### Task 17: Rename Abbreviated Identifiers And Remove The Null-Forgiving Operator

**Files:**
- Modify: `source/COMPEL/Services/Updates/VersionChecker.cs` in `ApplyUpdateAndRestart`
- Modify: `source/COMPEL/Services/ContentBroker/ContentBroker.cs` in `LocalFileMatchesManifestEntry`
- Modify: `C:\Users\SADS-810\Source\WILLOWMAKER\source\WILLOWMAKER.Core\Services\ContentBroker\ContentBroker.cs` in `LocalFileMatchesManifestEntry`
- Modify: `source/COMPEL.Tests/Services/Synchronisation/ContentBrokerTests.cs` in `A_Leftover_Partial_File_Is_Deleted_Before_The_Plan_Is_Reported`

**Interfaces:**
- Consumes: nothing.
- Produces: no signature changes.

- [x] **Step 1: Rename the temporary directory local**

```bash
sed -i 's/\btempDirectory\b/temporaryDirectory/g' source/COMPEL/Services/Updates/VersionChecker.cs
grep -n 'temporaryDirectory' source/COMPEL/Services/Updates/VersionChecker.cs
```

Expected: 11 lines listed, all inside `ApplyUpdateAndRestart`.

- [x] **Step 2: Rename the file information local in both content broker copies**

In `LocalFileMatchesManifestEntry`, in both files, replace:

```csharp
        FileInfo info = new (localPath);

        if (info.Exists is false)
            return false;

        if (info.Length != entry.Size)
            return false;
```

with:

```csharp
        FileInfo information = new (localPath);

        if (information.Exists is false)
            return false;

        if (information.Length != entry.Size)
            return false;
```

- [x] **Step 3: Verify parity between the two copies**

```bash
git diff --no-index source/COMPEL/Services/ContentBroker/ContentBroker.cs "C:/Users/SADS-810/Source/WILLOWMAKER/source/WILLOWMAKER.Core/Services/ContentBroker/ContentBroker.cs"
```

Expected: exactly three changed lines, the namespace line, the class summary line that says "orchestrator's" in COMPEL and "launcher's" in WILLOWMAKER, and the User-Agent line.

- [x] **Step 4: Replace the null-forgiving operator in the test**

In `ContentBrokerTests`, replace:

```csharp
                await Assert.That(recorder.Events[planIndex].Plan!.FilesToDelete).IsEqualTo(1);
```

with:

```csharp
                await Assert.That(recorder.Events[planIndex].Plan).IsNotNull();
                await Assert.That(recorder.Events[planIndex].Plan?.FilesToDelete).IsEqualTo(1);
```

- [x] **Step 5: Confirm no null-forgiving operator remains**

Run: `grep -rnP --include=*.cs '[\w\)\]]!(?=[\.\)\];,\s])' source/COMPEL source/COMPEL.Tests | grep -v '/obj/' | grep -vP '!='`
Expected: no output.

- [x] **Step 6: Build and test both repositories**

Run: `dotnet build source/COMPEL.slnx && dotnet test source/COMPEL.slnx`
Expected: build succeeds; 65 tests pass.

Run: `dotnet build "C:/Users/SADS-810/Source/WILLOWMAKER/source/WILLOWMAKER.slnx"`
Expected: build succeeds.

---

### Task 18: Normalise Line Endings

**Files:**
- Modify: every tracked text file under `source/` that still carries LF-only line endings. At the start of the plan these were `source/COMPEL.Tests/ContentBrokerTests.cs`, `LocationGuardTests.cs`, `VersionCheckerTests.cs`, `source/COMPEL/Services/ContentBroker/ContentBroker.cs`, `DeploymentManifest.cs`, `LocationGuard.cs`, `LocationSafetyVerdict.cs`, `UpdateGate.cs`, `VersionCheckResult.cs`, and `VersionChecker.cs`; earlier tasks moved several of them, so run the detection command rather than trusting this list.
- Modify: `C:\Users\SADS-810\Source\WILLOWMAKER\source\WILLOWMAKER.Core\Services\ContentBroker\ContentBroker.cs` and `C:\Users\SADS-810\Source\WILLOWMAKER\source\WILLOWMAKER.Core\Utilities\FileLockDetector.cs`, only if the detection command reports them as LF-only, so the parity-tracked copies keep identical bytes.

**Interfaces:**
- Consumes: nothing.
- Produces: no code changes; line endings only. `source/COMPEL/Assets/compel.ico` is binary and must not be touched.

`source/.editorconfig` sets `end_of_line = native:error`, which on Windows means CRLF, and every other source file already complies.

- [x] **Step 1: List the LF-only text files in both repositories**

```bash
for f in $(git ls-files source | grep -v '\.ico$'); do perl -ne 'BEGIN{$lf=0} $lf++ if !/\r$/ && /\n$/; END { print "$ARGV ($lf LF lines)\n" if $lf > 0 }' "$f"; done
for f in "C:/Users/SADS-810/Source/WILLOWMAKER/source/WILLOWMAKER.Core/Services/ContentBroker/ContentBroker.cs" "C:/Users/SADS-810/Source/WILLOWMAKER/source/WILLOWMAKER.Core/Utilities/FileLockDetector.cs"; do perl -ne 'BEGIN{$lf=0} $lf++ if !/\r$/ && /\n$/; END { print "$ARGV ($lf LF lines)\n" if $lf > 0 }' "$f"; done
```

Expected: the COMPEL files listed above at their current paths, plus whichever WILLOWMAKER files are LF-only.

- [x] **Step 2: Convert every listed file to CRLF**

For each path the commands printed:

```bash
perl -pi -e 's/\r?\n/\r\n/' "<path>"
```

- [x] **Step 3: Re-run the detection commands**

Run both commands from Step 1 again.
Expected: no output.

- [x] **Step 4: Confirm parity and that nothing but line endings changed**

```bash
git diff --no-index source/COMPEL/Services/ContentBroker/ContentBroker.cs "C:/Users/SADS-810/Source/WILLOWMAKER/source/WILLOWMAKER.Core/Services/ContentBroker/ContentBroker.cs"
git diff --no-index source/COMPEL/Utilities/FileLockDetector.cs "C:/Users/SADS-810/Source/WILLOWMAKER/source/WILLOWMAKER.Core/Utilities/FileLockDetector.cs"
git diff --ignore-cr-at-eol --stat
```

Expected: the first diff shows only the three permitted lines, the second only the namespace line, and the third shows no additional files beyond those earlier tasks changed.

- [x] **Step 5: Build and test**

Run: `dotnet build source/COMPEL.slnx && dotnet test source/COMPEL.slnx`
Expected: build succeeds; 65 tests pass.

---

### Task 19: Drop The Product Prefix From The Configuration Types

The three configuration types carry a `Compel` prefix that repeats the product name inside the product's own namespaces. `COMPEL.Configuration.CompelConfigurationFile` reads as COMPEL twice; `ConfigurationFile` in the same namespace says the same thing once. The prefix also breaks the repository's acronym rule, which would demand `COMPELConfigurationFile`.

**Files:**
- Rename: `source/COMPEL/Configuration/CompelConfigurationFile.cs` to `source/COMPEL/Configuration/ConfigurationFile.cs`
- Rename: `source/COMPEL/Configuration/CompelConfigurationLoader.cs` to `source/COMPEL/Configuration/ConfigurationLoader.cs`
- Rename: `source/COMPEL/Serialisation/CompelConfigurationJSONContext.cs` to `source/COMPEL/Serialisation/ConfigurationJSONContext.cs`
- Rename: `source/COMPEL.Tests/Configuration/CompelConfigurationLoaderTests.cs` to `source/COMPEL.Tests/Configuration/ConfigurationLoaderTests.cs`
- Modify: `source/COMPEL/COMPEL.cs` (8 references)

**Interfaces:**
- Consumes: nothing.
- Produces: `ConfigurationFile`, `ConfigurationLoader`, and `ConfigurationJSONContext` replace their `Compel`-prefixed names everywhere, with every member unchanged. The test class becomes `ConfigurationLoaderTests`. No namespace changes.

The type names are the only thing that changes. Do not touch the settings classes in the configuration file model (`UserNameSetting`, `PasswordSetting`, and the rest), the `ControlPlaneJSONContext`, any XML documentation wording other than where it names a renamed type, or any string literal. The file name `"COMPEL.json"` appears in messages and documentation and stays exactly as it is.

- [x] **Step 1: Rename the four files**

The git index must stay untouched, so use `mv`, not `git mv`.

```bash
mv source/COMPEL/Configuration/CompelConfigurationFile.cs source/COMPEL/Configuration/ConfigurationFile.cs
mv source/COMPEL/Configuration/CompelConfigurationLoader.cs source/COMPEL/Configuration/ConfigurationLoader.cs
mv source/COMPEL/Serialisation/CompelConfigurationJSONContext.cs source/COMPEL/Serialisation/ConfigurationJSONContext.cs
mv source/COMPEL.Tests/Configuration/CompelConfigurationLoaderTests.cs source/COMPEL.Tests/Configuration/ConfigurationLoaderTests.cs
```

- [x] **Step 2: Rename the types across the five files**

The three type names never appear as a substring of anything else, so a whole-word substitution is safe. `CompelConfigurationFile` must be substituted before `CompelConfigurationLoader` and `CompelConfigurationJSONContext` are, or not at all, because none is a prefix of another; the order below is therefore immaterial.

```bash
sed -i 's/\bCompelConfigurationFile\b/ConfigurationFile/g; s/\bCompelConfigurationLoader\b/ConfigurationLoader/g; s/\bCompelConfigurationJSONContext\b/ConfigurationJSONContext/g' \
    source/COMPEL/COMPEL.cs \
    source/COMPEL/Configuration/ConfigurationFile.cs \
    source/COMPEL/Configuration/ConfigurationLoader.cs \
    source/COMPEL/Serialisation/ConfigurationJSONContext.cs \
    source/COMPEL.Tests/Configuration/ConfigurationLoaderTests.cs
```

This also renames the source-generated property on the JSON context, because the generator names each property after its type: `WriteContext.CompelConfigurationFile` becomes `WriteContext.ConfigurationFile`, and `CompelConfigurationJSONContext.Default.CompelConfigurationFile` becomes `ConfigurationJSONContext.Default.ConfigurationFile`.

- [x] **Step 3: Rename the test class**

In `source/COMPEL.Tests/Configuration/ConfigurationLoaderTests.cs`, the class is named after the type it tests, so rename it too:

```bash
sed -i 's/\bCompelConfigurationLoaderTests\b/ConfigurationLoaderTests/g' source/COMPEL.Tests/Configuration/ConfigurationLoaderTests.cs
```

Note that Step 2 already rewrote `CompelConfigurationLoaderTests` to `ConfigurationLoaderTests` if its `\b` boundaries matched; run this command anyway, and if it changes nothing, that is the expected outcome.

- [x] **Step 4: Confirm no reference remains**

```bash
grep -rn 'CompelConfiguration' source --include=*.cs --include=*.csproj | grep -vE '/(obj|bin|\.vs)/'
find source -name 'CompelConfiguration*' -not -path '*/obj/*' -not -path '*/bin/*' -not -path '*/.vs/*'
```

Expected: no output from either.

- [x] **Step 5: Check the class summaries still read correctly**

The renamed types' XML summaries describe the configuration file; re-read all three and the test class summary, and fix any sentence that now reads oddly because it named the old type. Do not rewrite summaries that read correctly.

- [x] **Step 6: Build and test**

Run: `dotnet build source/COMPEL.slnx && dotnet test source/COMPEL.slnx`
Expected: build succeeds with 0 warnings; 65 tests pass, including `A_Generated_Default_File_Loads_Back_With_Its_Default_Values`, which proves the source-generated serialisation still round-trips under the new type name.

---

## Final Verification

- [x] `dotnet build source/COMPEL.slnx` succeeds with 0 warnings.
- [x] The LF detection command from Task 18 prints nothing for `source/`.
- [x] `dotnet test source/COMPEL.slnx` reports 65 passed.
- [x] `grep -rn 'Serilog' source --include=*.cs --include=*.csproj | grep -v '/obj/'` prints nothing.
- [x] `find source/COMPEL source/COMPEL.Tests -type d -not -path '*/bin*' -not -path '*/obj*' | while read directory; do files=$(find "$directory" -maxdepth 1 -type f | wc -l); folders=$(find "$directory" -mindepth 1 -maxdepth 1 -type d -not -name bin -not -name obj | wc -l); if [ "$files" -gt 0 ] && [ "$folders" -gt 0 ]; then echo "$directory"; fi; done` prints only `source/COMPEL` and `source/COMPEL.Tests`, the two project roots, whose loose files are the project files and the entry point.
- [x] The two comment detection commands from Task 15 print nothing.
- [x] `git diff --no-index` between each parity-tracked file and its WILLOWMAKER copy shows only the permitted lines.
- [x] Nothing has been committed in either repository: `git log -1 --format=%s` still prints `Upgrade To .NET 11` in COMPEL.

## Staging Slices

Nothing was committed during execution. A tree snapshot was written at every task boundary, so each slice is staged exactly with `git read-tree <snapshot>` and then committed; no hunk splitting is needed and every commit builds and passes on its own, because each snapshot is a state the suite was green at. The snapshots are unreachable git objects, kept by the default two-week prune window, so do not run `git gc --prune=now` before staging.

| # | Slice | Tasks | Snapshot | Size | Commit message |
| --- | --- | --- | --- | --- | --- |
| 1 | Logging | 1, 2 | `365f622` | 14 files | Replace Serilog And The Banner With A WILLOWMAKER-Style Logger |
| 2 | Type moves | 3, 4 | `dfcd11a` | 9 files | Move The Deployment And Self-Update Types Into Their Own Folders |
| 3 | Test folders | 5 | `7552e8f` | 12 files | Structure The Tests Into Directories |
| 4 | Synchronisation logging | 6 | `ec7db15` | 1 file | Align Synchronisation Logging With WILLOWMAKER |
| 5 | Lock detector | 7 | `41265d2` | 4 files | Port The File Lock Detector And Report The Processes Holding Failed Files |
| 6 | Version fallback | 8 | `a6fe0b4` | 2 files | Resolve The Distribution Version When Synchronisation Is Skipped |
| 7 | Supervisor | 9 | `b1ce985` | 1 file | Fix The Orphan Sweep On Linux And The Stale Process Reference |
| 8 | Validation | 10, 11 | `9db3289` | 4 files | Reject Whitespace In Credentials And Upper-Case The Server Location |
| 9 | Update script and loader | 12, 13 | `c9ec948` | 4 files | Escape Apostrophes In The Windows Update Script And Report Unreadable Configuration Files Cleanly |
| 10 | Configuration documentation | 14 | `b3728b9` | 2 files | Align The Configuration Documentation |
| 11 | Comment punctuation | 15 | `407af2c` | 16 files | Remove Trailing Punctuation From Comments |
| 12 | Naming | 17 | `5724d93` | 3 files | Rename Abbreviated Identifiers And Remove The Null-Forgiving Operator |
| 13 | Comment content | 16 | `8daee79` | 7 files | Tidy Comment Content And Casing |
| 14 | Review fixes | final review | `80010bb` | 6 files | Guard The Log File Write And Recover The Distribution Version |
| 15 | Configuration rename | 19 | `94bc2bf` | 5 files | Drop The Product Prefix From The Configuration Types |

Slice 9 can be split in two, because its files are disjoint: stage `source/COMPEL/Services/Updates` and `source/COMPEL.Tests/Services/Updates` from the snapshot for the update script, then the `Configuration` folders for the loader.

Task 18 gets no slice: both repositories normalise text to LF through `.gitattributes`, so converting the working copies to CRLF is invisible to git.

WILLOWMAKER takes one commit of its own, for the identifier rename mirrored into its content broker: **Rename Abbreviated Identifier In Content Broker**.

Not covered by any slice, and left for the user to decide: the plan document itself under `docs/`, which is untracked, and the four modified instruction files at the repository root, which were already modified before this work began.

## Owed Before Release

Neither check could be run during execution: both need an elevated console and the live CDN. Run both before tagging a release.

1. **Synchronisation log vocabulary.** Run COMPEL elevated from a directory holding only the build output and a configured `COMPEL.json`, so the location guard reports a baseline directory and a real synchronisation runs. Expect, in order: `[SYNCHRONISE] INIT: Fetching Manifest For Variant "was" From CDN`, `INIT: Manifest Version ... Lists ... File(s)`, `PLAN: ...`, a `PULL:` line per downloaded file, and `DONE: ...` last. While there, count the `SKIP:` lines on a second, up-to-date restart; that number decides the first deferred decision below.
2. **Distribution version fallback.** Run COMPEL elevated with `CDNSynchronisation` set to `false` and valid credentials. Expect `[SYNCHRONISE] SKIP: Synchronisation Skipped (Manual Override)`, then the two `INIT:` lines, and no `No Distribution Version Is Known` warning from the ping responder.

What was verified instead: the Debug build's first-run and unelevated paths, and a real Native AOT Release publish whose binary generated `COMPEL.json` on first run leaving no log or lock file, then exercised the location guard's live path on its second run.

## Deferred By Decision

These review findings are intentionally not fixed by this plan.

- **Local up-to-date check hashes with SHA-256 regardless of the manifest algorithm.** Deliberate: the check was simplified to match WILLOWMAKER's production code, the manifests are SHA-256 and controlled by the same owner, and the download path still verifies with the declared algorithm. Revisit only if a manifest ever declares another algorithm.
- **Race between `/sync` and `/instances/start`.** The window is the few microseconds between the endpoint's state check and the synchroniser raising its flag, and closing it needs coordination across two services. Not worth the coupling.
- **`FirstOrDefault` in `AddressResolver`.** DNS legitimately returns several addresses, so the first IPv4 record is the correct semantics and `SingleOrDefault` would throw.
- **Port-range boundary one instance more permissive than the legacy check.** Intentional: the new check treats the top of the window as usable.
- **The one-word difference between the two content broker summaries.** "orchestrator's" describes COMPEL and "launcher's" describes WILLOWMAKER; both copies are otherwise identical.
- **WILLOWMAKER's `COMMAND____` and `PARAMETERS_` categories.** Not ported because the manager command line carries the account password and must not be logged.
- **A per-file `SKIP:` line for every up-to-date file on every restart, in an unrotated log.** The content broker reports every up-to-date manifest entry, so a steady-state restart writes one line per distribution file. This matches WILLOWMAKER, but WILLOWMAKER is a launcher a person runs once and COMPEL is a service that systemd restarts. Summarising the up-to-date case as one line would depart from parity, so the decision is the user's.
- **The skip branches block start-up for up to thirty seconds on a disconnected machine.** Resolving the distribution version before the ready gate is what makes the ping responder's missing-version warning truthful, and the content broker's thirty-second timeout is inside the parity-locked file. The cost only appears with synchronisation disabled and no network, which is a development configuration.
- **HTTP request logging.** Dropped with Serilog; WILLOWMAKER has no equivalent and the control plane's health polling would have flooded the log.
