namespace COMPEL;

internal static class CHECKS
{
    internal static void CheckPriviledgeElevation()
    {
        if (CONTEXT.ProcessIsElevated.Equals(false))
        {
            Console.WriteLine(@"The Game Server Manager And The Proxy Manager Require Elevated Privileges To Run Correctly");
            Console.WriteLine(@"Start COMPEL As Administrator");
            Console.ReadKey();
            Environment.Exit(1);
        }
    }

    internal static void CheckConfiguration()
    {
        CheckUserName();
        CheckPassword();
        CheckInstances();
        CheckHostingEnvironment();
        CheckLocation();
        CheckServerNamePrefix();
        CheckUseProxy();
        CheckPortRangeOffset();
        CheckRuntimeArtefactsPath();
    }

    private static void CheckUserName()
    {
        string? userName = CONTEXT.JSONConfiguration.UserName?.Value;

        if (userName is null)
        {
            Console.WriteLine(@"Missing Configuration Value For ""UserName""");
            Console.ReadKey();
            Environment.Exit(1);
        }

        else { Console.WriteLine($"UserName: {userName}"); }
    }

    private static void CheckPassword()
    {
        string? password = CONTEXT.JSONConfiguration.Password?.Value;

        if (password is null)
        {
            Console.WriteLine(@"Missing Configuration Value For ""Password""");
            Console.ReadKey();
            Environment.Exit(1);
        }
    }

    private static void CheckInstances()
    {
        int? instances = CONTEXT.JSONConfiguration.Instances?.Value;

        if (instances is null)
        {
            Console.WriteLine(@"Missing Configuration Value For ""Instances""");
            Console.ReadKey();
            Environment.Exit(1);
        }

        if (instances is 0)
        {
            Console.WriteLine("Number Of Instances Is Zero");
            Console.ReadKey();
            Environment.Exit(1);
        }

        if (instances < 0)
        {
            Console.WriteLine("Number Of Instances Is Negative");
            Console.ReadKey();
            Environment.Exit(1);
        }

        if (instances > Environment.ProcessorCount)
        {
            Console.WriteLine("Number Of Instances Is Greater Than Nummber Of Logical Processors");
            Console.WriteLine("Hosting More Than One Instance Per Logical Processor Will Result In Sub-Optimal Performance");
            Console.ReadKey();
            Environment.Exit(1);
        }

        else { Console.WriteLine($"Number Of Instances To Spawn: {instances}"); }
    }

    private static void CheckHostingEnvironment()
    {
        string? hostingEnvironment = CONTEXT.JSONConfiguration.HostingEnvironment?.Value;

        if (hostingEnvironment is null)
        {
            Console.WriteLine(@"Missing Configuration Value For ""HostingEnvironment""");
            Console.ReadKey();
            Environment.Exit(1);
        }

        if (new[] { "LOCAL", "PUBLIC" }.Contains(hostingEnvironment.ToUpper()).Equals(false))
        {
            Console.WriteLine($@"""{hostingEnvironment}"" Is Not A Valid Hosting Environment");
            Console.WriteLine(@"Valid Hosting Environment: 1) local\LOCAL, 2) public\PUBLIC");
            Console.ReadKey();
            Environment.Exit(1);
        }

        else { Console.WriteLine($"Hosting Environment: {hostingEnvironment.ToUpper()}"); }
    }

    private static void CheckLocation()
    {
        string? location = CONTEXT.JSONConfiguration.Location?.Value;

        if (location is null)
        {
            Console.WriteLine(@"Missing Configuration Value For ""Location""");
            Console.ReadKey();
            Environment.Exit(1);
        }

        string[] supportedLocations = { "USW", "USE", "EU", "AU", "BR", "RU", "SEA", "NEWERTH" };

        if (supportedLocations.Contains(location.ToUpper()).Equals(false))
        {
            Console.WriteLine($@"""{location}"" Is Not A Valid Location");
            Console.WriteLine(@"Valid Locations: 1) usw\USW, 2) use\USE, 3) eu\EU, 4) au\AU, 5) br\BR, 6) ru\RU, 7) sea\SEA");
            Console.ReadKey();
            Environment.Exit(1);
        }

        else { Console.WriteLine($"Location: {location.ToUpper()}"); }
    }

    private static void CheckServerNamePrefix()
    {
        string? serverNamePrefix = CONTEXT.JSONConfiguration.ServerNamePrefix?.Value;

        if (serverNamePrefix is null)
        {
            Console.WriteLine(@"Missing Configuration Value For ""ServerNamePrefix""");
            Console.ReadKey();
            Environment.Exit(1);
        }

        else { Console.WriteLine($"Server Name Prefix: {serverNamePrefix}"); }
    }

    private static void CheckUseProxy()
    {
        bool? useProxy = CONTEXT.JSONConfiguration.UseProxy?.Value;

        if (useProxy is null)
        {
            Console.WriteLine(@"Missing Configuration Value For ""UseProxy""");
            Console.ReadKey();
            Environment.Exit(1);
        }

        else { Console.WriteLine($"Use Proxy: {useProxy.Value.ToString().ToUpper()}"); }
    }

    private static void CheckPortRangeOffset()
    {
        int? offset = CONTEXT.JSONConfiguration.PortRangeOffset?.Value;

        if (offset is null)
        {
            Console.WriteLine(@"Missing Configuration Value For ""PortRangeOffset""");
            Console.ReadKey();
            Environment.Exit(1);
        }

        if (offset < 0)
        {
            Console.WriteLine("Port Range Offset Is Negative");
            Console.ReadKey();
            Environment.Exit(1);
        }

        int minGamePort, maxGamePort, minVoicePort, maxVoicePort;

        if ((bool)CONTEXT.JSONConfiguration.UseProxy!.Value!)
        {
            minGamePort = 21235;
            maxGamePort = 21335;
            minVoicePort = 21435;
            maxVoicePort = 21535;
        }

        else
        {
            minGamePort = 11235;
            maxGamePort = 11335;
            minVoicePort = 11435;
            maxVoicePort = 11535;
        }

        int? instances = CONTEXT.JSONConfiguration.Instances!.Value;

        if (minGamePort + offset + instances > maxGamePort || minVoicePort + offset + instances > maxVoicePort)
        {
            Console.WriteLine(instances is not 1
                ? $"An Offset Of {offset} Causes Ports For {instances} Instances To Bleed Outside Of The Allowed Port Range"
                : $"An Offset Of {offset} Causes Ports For {instances} Instance To Bleed Outside Of The Allowed Port Range");

            Console.ReadKey();
            Environment.Exit(1);
        }

        else { Console.WriteLine($"Port Range Offset: {offset}"); }
    }

    private static void CheckRuntimeArtefactsPath()
    {
        string? runtimeArtefactsPath = CONTEXT.JSONConfiguration.RuntimeArtefactsPath?.Value;

        if (runtimeArtefactsPath is null)
        {
            Console.WriteLine(@"Missing Configuration Value For ""RuntimeArtefactsPath""");
            Console.ReadKey();
            Environment.Exit(1);
        }

        string[] supportedAliases = { "TEMP", "DOCUMENTS" };

        bool isAlias = supportedAliases.Contains(runtimeArtefactsPath.ToUpper());
        bool isPath = Uri.TryCreate(runtimeArtefactsPath, UriKind.Absolute, out Uri? result);

        if (isAlias.Equals(false) && isPath.Equals(false))
        {
            Console.WriteLine($@"""{runtimeArtefactsPath}"" Is Not A Valid Runtime Artefacts Path");
            Console.WriteLine(@"Valid Paths: 1) a fully qualified path, 2) temp\TEMP, 3) documents\DOCUMENTS");
            Console.ReadKey();
            Environment.Exit(1);
        }

        else { Console.WriteLine($"Runtime Artefacts Path: {runtimeArtefactsPath.ToUpper()}"); }
    }
}
