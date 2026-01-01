CHECKS.CheckPriviledgeElevation();

CHECKS.CheckConfiguration();

HANDLERS.KillZombiesAndOrphansAndPreventDoppelgangers();

HANDLERS.HandleMasterServerPing();

Console.WriteLine($"Number Of Logical Processors: {Environment.ProcessorCount}");

Dictionary<string,object> settings = new();

int instancesCount = Convert.ToInt32(CONTEXT.JSONConfiguration.Instances!.Value);

int startingServerGamePort = 11235 + CONTEXT.JSONConfiguration.PortRangeOffset!.Value;
int startingServerVoicePort = 11435 + CONTEXT.JSONConfiguration.PortRangeOffset!.Value;

settings["man_masterLogin"] = CONTEXT.JSONConfiguration.UserName!.Value! + ":"; // Add ':' ('|', '$' or '#' do not work) so that we can map game server instances to an account name (e.g. KONGOR:1, KONGOR:2, KONGOR:3, etc.). The server manager automatically appends an incremental index to this value.
settings["man_masterPassword"] = CONTEXT.JSONConfiguration.Password!.Value!;
settings["man_numSlaveAccounts"] = instancesCount.ToString();
settings["man_startServerPort"] = startingServerGamePort.ToString();
settings["man_endServerPort"] = (startingServerGamePort + instancesCount - 1).ToString();
settings["man_voiceProxyStartPort"] = startingServerVoicePort.ToString(); // This setting is incorrectly named. It is essentially the "man_voiceStartPort".
settings["man_voiceProxyEndPort"] = (startingServerVoicePort + instancesCount - 1).ToString(); // This setting is incorrectly named. It is essentially the "man_voiceEndPort".
settings["man_maxServers"] = Environment.ProcessorCount.ToString();
settings["man_enableProxy"] = (bool)CONTEXT.JSONConfiguration.UseProxy!.Value! ? "true" : "false";
settings["man_broadcastSlaves"] = "true";
settings["man_autoServersPerCPU"] = "1";
settings["man_allowCPUs"] = string.Join(',', Enumerable.Range(0, Environment.ProcessorCount).Select(number => number.ToString()));

// Enables On-Demand Replay Uploads
settings["man_uploadToS3OnDemand"] = "1";

// Disables Partial Replay Uploads
settings["man_uploadToCDNOnDemand"] = "0";

// Any Server Configuration Options Other Than The Ones Below Get Completely Ignored
settings["svr_name"] = HANDLERS.ServerNameWithWhiteSpaceCharacters(CONTEXT.JSONConfiguration.ServerNamePrefix!.Value!);
settings["svr_location"] = CONTEXT.JSONConfiguration.Location!.Value!;
settings["svr_ip"] = CONTEXT.ServerAddress;

// Setting The Server Manager's Affinity To "-1" Is Required, To Allow It To Assign Affinity For Child Processes
settings["host_affinity"] = "-1";

// Enable Auto-Update
settings["upd_checkForUpdates"] = "true";

// Port For Listening To Pings From The Master Server
settings["svr_port"] = ((bool)CONTEXT.JSONConfiguration.UseProxy!.Value! ? 21234 : 11234) + CONTEXT.JSONConfiguration.PortRangeOffset!.Value;

List<string> arguments = new()
{
    "-manager", "-noconfig", "-execute",
    '"' + string.Join(';', settings.Select(pair => $@"Set {pair.Key} {pair.Value}")) + '"',
    $"-masterserver {CONTEXT.MasterServerAddressAndPort}"
};

string managerConfigurationPath = Path.Combine(CONTEXT.RuntimeArtefactsPath, "DOCUMENTS", "HEROES OF NEWERTH x64", "GAME");

Directory.CreateDirectory(managerConfigurationPath);

Console.WriteLine($"Runtime Artefacts Location: {Path.Combine(CONTEXT.RuntimeArtefactsPath)}");

if ((bool)CONTEXT.JSONConfiguration.UseProxy!.Value!)
{
    StringBuilder proxyConfiguration = new StringBuilder()
        .AppendLine($"count={instancesCount}")
        .AppendLine($"ip={CONTEXT.ServerAddress}")
        .AppendLine($"startport={startingServerGamePort}")
        .AppendLine($"startvoicePort={startingServerVoicePort}")
        .AppendLine($"region=naeu");

    string proxyConfigurationPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HonProxyManager");

    Directory.CreateDirectory(proxyConfigurationPath);

    File.WriteAllText(Path.Combine(proxyConfigurationPath, "config.cfg"), proxyConfiguration.ToString());

    try
    {
        Process proxyProcess = Process.Start("proxymanager.exe");
    }

    catch
    {
        Console.WriteLine("Failed To Start The HON Proxy Manager");
        Console.ReadKey();
        Environment.Exit(1);
    }
}

ProcessStartInfo updaterProcessInfo = new("hon_x64.exe")
{
    EnvironmentVariables = { ["USERPROFILE"] = Path.Combine(CONTEXT.RuntimeArtefactsPath) },
    Arguments = string.Join(' ', arguments)
};

Process? updaterProcess = Process.Start(updaterProcessInfo);

if (updaterProcess is null)
{
    Console.WriteLine("Failed To Start The HON Updater");
    Console.ReadKey();
    Environment.Exit(1);
}

if (instancesCount is 1)
{
    int gamePort, voicePort;

    if ((bool)CONTEXT.JSONConfiguration.UseProxy!.Value!)
    {
        gamePort = startingServerGamePort + 10000;
        voicePort = startingServerVoicePort + 10000;
    }

    else
    {
        gamePort = startingServerGamePort;
        voicePort = startingServerVoicePort;
    }

    Console.WriteLine($@"Server Manager Has Spawned {instancesCount} Game Server Instance On Game Port {gamePort} And Voice Port {voicePort}");
}

else
{
    string gamePortRange, voicePortRange;

    if ((bool)CONTEXT.JSONConfiguration.UseProxy!.Value!)
    {
        gamePortRange = $"{startingServerGamePort + 10000}-{startingServerGamePort + 10000 + instancesCount - 1}";
        voicePortRange = $"{startingServerVoicePort + 10000}-{startingServerVoicePort + 10000 + instancesCount - 1}";
    }

    else
    {
        gamePortRange = $"{startingServerGamePort}-{startingServerGamePort + instancesCount - 1}";
        voicePortRange = $"{startingServerVoicePort}-{startingServerVoicePort + instancesCount - 1}";
    }

    Console.WriteLine($@"Server Manager Has Spawned {instancesCount} Game Server Instances On Game Port Range {gamePortRange} And Voice Port Range {voicePortRange}");
}

// Anti-DDoS And Anti-Cheat Protection Is Supported Natively Only On Port Range 20000-29999

HANDLERS.StartRespondingToPings();

await HANDLERS.RunMaintenanceLoop();
