// Configuration Is A Single Self-Describing "COMPEL.json" File Beside The Executable; On First Run It Is Created With Defaults And The Process Stops So The Operator Can Configure It
// This Path Runs Before The Logger Exists Because The Release Workflow Runs The Published Binary Once To Generate The Shipped Configuration File And Fails If Anything Else Is Left Behind
if (ConfigurationLoader.Exists() is false)
{
    ConfigurationLoader.CreateDefault();

    Console.WriteLine($@"Created A Default Configuration File At ""{ConfigurationLoader.ResolvePath()}""; Set At Least ""UserName"" And ""Password"", Then Start COMPEL Again");

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

ConfigurationFile configuration;

try
{
    configuration = ConfigurationLoader.Load();
}

catch (InvalidOperationException exception)
{
    logger.Log(LogCategory.Initialise, exception.Message);
    logger.Log(LogCategory.Initialise, $@"Fix Or Delete ""{ConfigurationLoader.ResolvePath()}"" And Start COMPEL Again");

    return;
}

string installationDirectory = DistributionSynchronisationService.ResolveInstallationDirectory(new CDNOptions().InstallationDirectory);

// COMPEL Mirrors The Match Server Distribution Into Its Installation Directory, So It Refuses To Start From A Directory Whose Contents Are Neither An Existing Installation Nor A Fresh Deployment; The Synchronisation's Deletion Pass Would Otherwise Remove Unrelated Files
LocationGuard.Result locationSafety = LocationGuard.AssessLocationSafety(installationDirectory);

logger.Log(LogCategory.Guard, locationSafety.Reason);

if (locationSafety.Verdict is LocationSafetyVerdict.Unsafe)
{
    foreach (string foreignEntry in LocationGuard.ApplyForeignEntriesDisplayCap(locationSafety.ForeignEntries))
        logger.Log(LogCategory.Guard, foreignEntry);

    logger.Log(LogCategory.Guard, "COMPEL Will Not Start From This Directory Because It Contains The Unrelated Entries Listed Above, Which The Distribution Synchronisation Would Delete");
    logger.Log(LogCategory.Guard, "Move COMPEL To An Empty Directory Or To An Existing Match Server Installation, Then Start It Again");

    return;
}

// The Manager Spawns Each Server Instance With An Unquoted Executable Path, Which Heroes Of Newerth Only Parses Correctly When That Path Contains A Whitespace Character
// Refused Up Front Because The Symptom Is Otherwise Silent And Very Hard To Read: The Manager Starts And Registers Normally, But Every Instance Comes Up As A Game Client And Never Binds Its Game Port, So The Host Advertises Servers That No Client Can Join
if (HeroesOfNewerthExecutable.CanLaunchInstancesFrom(installationDirectory) is false)
{
    logger.Log(LogCategory.Guard, $@"COMPEL Will Not Start From ""{installationDirectory}"" Because The Path Contains No Whitespace Character");
    logger.Log(LogCategory.Guard, "Heroes Of Newerth Requires A Whitespace Character Somewhere In The Path It Is Launched From, Otherwise The Match Server Instances Never Start Correctly");
    logger.Log(LogCategory.Guard, @"Move COMPEL To A Directory Whose Path Contains A Whitespace Character (For Example ""C:\HoN Match Server""), Then Start It Again");

    return;
}

// The Control Plane Port Is Validated Here, Ahead Of Kestrel, So An Out-Of-Range Value Produces A Clear Message Rather Than An Unhandled Bind Failure Or A Silent Bind To An Arbitrary Ephemeral Port
if (configuration.ControlPlanePort.Value is < 1 or > 65535)
{
    logger.Log(LogCategory.Initialise, $"The Configured Control Plane Port ({configuration.ControlPlanePort.Value}) Is Invalid; It Must Be Between 1 And 65535");
    logger.Log(LogCategory.Initialise, $@"Fix ""{ConfigurationLoader.ResolvePath()}"" And Start COMPEL Again");

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

    logger.Log(LogCategory.Initialise, $@"Fix ""{ConfigurationLoader.ResolvePath()}"" And Start COMPEL Again");

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
