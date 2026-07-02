// Serilog Is Referenced Only Here (For The Logger Configuration), And Its Root Namespace Contains An "ILogger" That Would Clash With "Microsoft.Extensions.Logging.ILogger" Used Throughout The Rest Of The Code, So These Directives Are Kept Local Rather Than Global.
using Serilog;
using Serilog.Events;

Banner.Write();

// Configuration Is A Single Self-Describing "COMPEL.json" File Beside The Executable. On First Run It Is Created With Defaults And The Process Stops So The Operator Can Configure It.
if (CompelConfigurationLoader.Exists() is false)
{
    CompelConfigurationLoader.CreateDefault();

    Console.WriteLine($@"Created a default configuration file at ""{CompelConfigurationLoader.ResolvePath()}"". Set at least ""UserName"" and ""Password"", then start COMPEL again.");

    return;
}

CompelConfigurationFile configuration;

try
{
    configuration = CompelConfigurationLoader.Load();
}

catch (InvalidOperationException exception)
{
    Console.WriteLine(exception.Message);
    Console.WriteLine($@"Fix Or Delete ""{CompelConfigurationLoader.ResolvePath()}"" And Start COMPEL Again.");

    return;
}

// The Control Plane Port Is Validated Here, Ahead Of Kestrel, So An Out-Of-Range Value Produces A Clear Message Rather Than An Unhandled Bind Failure Or A Silent Bind To An Arbitrary Ephemeral Port.
if (configuration.ControlPlanePort.Value is < 1 or > 65535)
{
    Console.WriteLine($@"The Configured Control Plane Port ({configuration.ControlPlanePort.Value}) Is Invalid; It Must Be Between 1 And 65535.");
    Console.WriteLine($@"Fix ""{CompelConfigurationLoader.ResolvePath()}"" And Start COMPEL Again.");

    return;
}

// Refuse To Start If Another COMPEL Instance Is Already Running Against This Installation.
string lockFilePath = Path.Combine(AppContext.BaseDirectory, "COMPEL.lock");

if (SingleInstanceGuard.TryAcquire(lockFilePath, out SingleInstanceGuard singleInstanceGuard) is false)
{
    Console.WriteLine("Another COMPEL Instance Is Already Running Against This Installation.");

    return;
}

// "CreateSlimBuilder" Initialises The Host With Only The Features Native AOT Needs, Keeping The Published Binary Small.
WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(args);

// Bind The Control Plane To Its Configured Port.
builder.WebHost.UseUrls($"http://0.0.0.0:{configuration.ControlPlanePort.Value}");

// Options: The Host-Facing Settings Come From "COMPEL.json" (Validated At Startup); The Infrastructure Settings Use Built-In Defaults.
builder.Services.AddSingleton<IValidateOptions<MatchServerManagerOptions>, MatchServerManagerOptionsValidator>();

builder.Services.AddOptions<MatchServerManagerOptions>().Configure(options =>
{
    options.UserName             = configuration.UserName.Value;
    options.Password             = configuration.Password.Value;
    options.Instances            = configuration.Instances.Value;
    options.Gateway              = configuration.Gateway.Value;
    options.Location             = configuration.Location.Value;
    options.ServerNamePrefix     = configuration.ServerNamePrefix.Value;
    options.UseProxy             = configuration.UseProxy.Value;
    options.PortRangeOffset      = configuration.PortRangeOffset.Value;
    options.RuntimeArtefactsPath = configuration.RuntimeArtefactsPath.Value;
}).ValidateOnStart();

builder.Services.AddOptions<ControlPlaneOptions>().Configure(options => options.AuthenticationToken = configuration.AuthenticationToken.Value);

builder.Services.AddOptions<CDNOptions>().Configure(options => options.Synchronisation = configuration.CDNSynchronisation.Value);

// JSON: Source-Generated Serialisation Metadata For The Minimal-API Responses (Required Under Native AOT).
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.TypeInfoResolverChain.Insert(0, ControlPlaneJSONContext.Default));

// Logging: Serilog In Code (Native AOT Safe). Console Plus A Single "COMPEL.log" File Beside The Executable.
builder.Logging.ClearProviders();

string logFilePath = Path.Combine(AppContext.BaseDirectory, "COMPEL.log");

builder.Services.AddSerilog(loggerConfiguration =>
{
    loggerConfiguration
        .MinimumLevel.Information()
        .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
        .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
        .Enrich.FromLogContext()
        .WriteTo.Console()
        // Roll The File Daily And On A Size Cap, Retaining A Bounded Number Of Files, So Logging Does Not Silently Stop At The Default Single-File Size Limit On A Long-Running Host.
        .WriteTo.File
        (
            logFilePath,
            rollingInterval: RollingInterval.Day,
            rollOnFileSizeLimit: true,
            fileSizeLimitBytes: 100L * 1024 * 1024,
            retainedFileCountLimit: 14,
            flushToDiskInterval: TimeSpan.FromSeconds(1)
        );
});

// Firewall Controller: The Windows Implementation When Elevated, Otherwise A No-Op (Application-Layer Bans Still Apply).
if (OperatingSystem.IsWindows() && Environment.IsPrivilegedProcess)
    builder.Services.AddSingleton<IFirewallController, WindowsFirewallController>();
else
    builder.Services.AddSingleton<IFirewallController, NoOperationFirewallController>();

// Domain Services.
// The Port Plan Is A Pure Function Of The Options, So It Is Registered Once As The Single Source Of Truth Shared By The Supervisor, The Proxy, And The Ping Responder.
builder.Services.AddSingleton(serviceProvider => new PortPlan(serviceProvider.GetRequiredService<IOptions<MatchServerManagerOptions>>().Value));
builder.Services.AddSingleton<AddressResolver>();
builder.Services.AddSingleton<ArtefactsLocator>();
builder.Services.AddSingleton<DistributionSynchronisationService>();
builder.Services.AddSingleton<MatchServerManagerSupervisor>();
builder.Services.AddSingleton<UDPProxyService>();
builder.Services.AddSingleton<UDPPingResponder>();

// Hosted Services. The Stateful Singletons Are Re-Registered As Hosted Services So The Control Plane And The Health Checks Can Resolve Them To Read Their Live State.
builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<DistributionSynchronisationService>());
builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<MatchServerManagerSupervisor>());
builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<UDPProxyService>());
builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<UDPPingResponder>());
builder.Services.AddHostedService<MaintenanceService>();

// Health Checks. The Ping Responder And Proxy Checks Surface A Port-Bind Failure Via "/health" Instead Of It Being Visible Only In A Log Line.
builder.Services.AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy(), [ "live" ])
    .AddCheck<UDPPingResponderHealthCheck>("ping-responder")
    .AddCheck<UDPProxyServiceHealthCheck>("proxy");

// The Options Validation Failure Is Caught Here And Reported As A Clean Message, Matching How The Configuration-Load Path Above Reports Problems Rather Than Surfacing As A Host Startup Crash.
try
{
    WebApplication application = builder.Build();

    // Trigger The Configured Options Validation Before The Host Starts, So A Configuration Problem Is Reported By The Handler Below Instead Of Being Logged As A Host Startup Failure With A Stack Trace.
    _ = application.Services.GetRequiredService<IOptions<MatchServerManagerOptions>>().Value;

    // Released Explicitly On A Graceful Shutdown; A Crash Or A Forced Kill Still Releases The Underlying File Handle At The Operating-System Level.
    application.Lifetime.ApplicationStopping.Register(() => singleInstanceGuard.Dispose());

    application.UseSerilogRequestLogging();

    if (Environment.IsPrivilegedProcess is false)
        application.Logger.LogWarning("COMPEL Is Not Running With Elevated Privileges; Processor Affinity And Firewall Integration Will Be Unavailable");

    application.MapHealthChecks("/health");
    application.MapHealthChecks("/alive", new HealthCheckOptions { Predicate = registration => registration.Tags.Contains("live") });

    application.MapControlPlaneEndpoints();

    application.Run();
}

catch (IOException exception)
{
    singleInstanceGuard.Dispose();

    // Kestrel Surfaces A Failure To Bind The Control-Plane Port (For Example When It Is Already In Use) As An "IOException" During Startup; It Is Reported Cleanly Here Rather Than As An Unhandled Stack Trace.
    Console.WriteLine($@"COMPEL Could Not Start Its HTTP Control Plane On Port {configuration.ControlPlanePort.Value}: {exception.Message}");
    Console.WriteLine(@"Ensure The Port Is Free Or Change ""ControlPlanePort"", Then Start COMPEL Again.");

    return;
}

catch (OptionsValidationException exception)
{
    singleInstanceGuard.Dispose();

    Console.WriteLine("The COMPEL Configuration Is Invalid:");

    foreach (string failure in exception.Failures)
        Console.WriteLine($"    - {failure}");

    Console.WriteLine($@"Fix ""{CompelConfigurationLoader.ResolvePath()}"" And Start COMPEL Again.");

    return;
}
