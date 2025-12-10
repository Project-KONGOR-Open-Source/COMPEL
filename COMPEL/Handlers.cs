namespace COMPEL;

internal static class HANDLERS
{
    // basic wrapper around UDP Socket that listens to packets sent by the game to the launcher and responds to ping requests
    internal class PingHandler
    {
        private EndPoint Sender = new IPEndPoint(IPAddress.Any, 0);

        private readonly Socket Socket;

        private readonly byte[] Buffer = new byte[1460]; // max UDP packet size
        private readonly byte[] Response;

        internal PingHandler()
        {
            Socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            Socket.Bind(new IPEndPoint(IPAddress.Any, ((bool)CONTEXT.JSONConfiguration.UseProxy!.Value! ? 21234 : 11234) + CONTEXT.JSONConfiguration.PortRangeOffset!.Value));

            byte[] serverName = Encoding.UTF8.GetBytes(CONTEXT.JSONConfiguration.ServerNamePrefix!.Value!);

            // read version number from the server binary
            byte[] hon_x64 = File.ReadAllBytes("hon_x64.exe");
            byte[] version = new byte[12]; // at most 12 bytes long
            int versionLength = 0;
            const int versionOffset = 88544;

            while (true)
            {
                byte b = hon_x64[versionOffset + versionLength * 2];
                if (b == 0) break;

                version[versionLength] = b;
                ++versionLength;
            }

            int messageSize = 69 + serverName.Length + versionLength;

            Response = new byte[messageSize];

            Response[42] = 0x01; // unreliabe flag
            Response[43] = 0x66; // pong message type

            // write server name
            Array.Copy(serverName, 0, Response, 46, serverName.Length);

            // write server version
            Array.Copy(version, 0, Response, 50 + serverName.Length, versionLength);
        }

        internal void Start()
        {
            Socket.BeginReceiveFrom(Buffer, 0, Buffer.Length, SocketFlags.None, ref Sender, ReceiveCallback, this);
        }

        internal static void ReceiveCallback(IAsyncResult ar)
        {
            if (ar.AsyncState is not PingHandler pingHandler) return;

            Socket socket = pingHandler.Socket;
            EndPoint sender = pingHandler.Sender;
            byte[] buffer = pingHandler.Buffer;

            try
            {
                int bytesReceived = socket.EndReceiveFrom(ar, ref sender);

                // ping request length is 46 bytes (40 bytes of watermark and 6 bytes of payload)
                if (bytesReceived != 46)
                {
                    Console.WriteLine("Unknown Message");
                    return;
                }

                if (buffer[43] != 0xCA)
                {
                    Console.WriteLine("Unknown Message");
                    return;
                }

                // write challenge (appears to be optional but helps randomize packets that we send)
                byte[] response = pingHandler.Response;
                response[44] = buffer[44];
                response[45] = buffer[45];

                // done
                socket.SendTo(response, sender);
            }

            catch (Exception e)
            {
                Console.WriteLine(e);
            }

            finally
            {
                // listen for the next UDP packet
                socket.BeginReceiveFrom(buffer, 0, buffer.Length, SocketFlags.None, ref sender, ReceiveCallback, pingHandler);
            }
        }
    }

    internal static void StartRespondingToPings()
    {
        new PingHandler().Start();
    }

    internal static CONFIGURATION HandleJSON()
    {
        const string file = "COMPEL.JSON";

        if (File.Exists(file).Equals(false))
        {
            CONFIGURATION content = new()
            {
                UserName = new UserName { Value = "USERNAME" },
                Password = new Password { Value = "PASSWORD" },
                Instances = new Instances { Value = 1 },
                HostingEnvironment = new HostingEnvironment { Value = "PUBLIC" },
                Location = new Location { Value = "EU" },
                ServerNamePrefix = new ServerNamePrefix { Value = "KONGOR ARENA" },
                UseProxy = new UseProxy { Value = true },
                PortRangeOffset = new PortRangeOffset { Value = 0 },
                RuntimeArtefactsPath = new RuntimeArtefactsPath { Value = "TEMP" }
            };

            string serialized = JsonSerializer.Serialize(content, new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

            File.WriteAllText(file, serialized);
        }

        CONFIGURATION? json = JsonSerializer.Deserialize<CONFIGURATION>(File.ReadAllText(file));

        if (json is null)
        {
            Console.WriteLine("Invalid JSON Configuration");
            Console.WriteLine("Delete The JSON Configuration File, And COMPEL Will Recreate It With The Default Values");
            Console.ReadKey();
            Environment.Exit(1);
        }

        if (json.UserName is null)
        {
            Console.WriteLine(@"Missing ""UserName"" Configuration Key");
            Console.ReadKey();
            Environment.Exit(1);
        }

        if (json.Password is null)
        {
            Console.WriteLine(@"Missing ""Password"" Configuration Key");
            Console.ReadKey();
            Environment.Exit(1);
        }

        if (json.Instances is null)
        {
            Console.WriteLine(@"Missing ""Instances"" Configuration Key");
            Console.ReadKey();
            Environment.Exit(1);
        }

        if (json.HostingEnvironment is null)
        {
            Console.WriteLine(@"Missing ""HostingEnvironment"" Configuration Key");
            Console.ReadKey();
            Environment.Exit(1);
        }

        if (json.Location is null)
        {
            Console.WriteLine(@"Missing ""Location"" Configuration Key");
            Console.ReadKey();
            Environment.Exit(1);
        }

        if (json.ServerNamePrefix is null)
        {
            Console.WriteLine(@"Missing ""ServerNamePrefix"" Configuration Key");
            Console.ReadKey();
            Environment.Exit(1);
        }

        if (json.UseProxy is null)
        {
            Console.WriteLine(@"Missing ""UseProxy"" Configuration Key");
            Console.ReadKey();
            Environment.Exit(1);
        }

        if (json.PortRangeOffset is null)
        {
            Console.WriteLine(@"Missing ""PortRangeOffset"" Configuration Key");
            Console.ReadKey();
            Environment.Exit(1);
        }

        if (json.RuntimeArtefactsPath is null)
        {
            Console.WriteLine(@"Missing ""RuntimeArtefactsPath"" Configuration Key");
            Console.ReadKey();
            Environment.Exit(1);
        }

        return json;
    }

    internal static string GetMasterServerAddress() =>
        CONTEXT.JSONConfiguration.HostingEnvironment!.Value!.ToUpper().Equals("LOCAL")
            ? "127.0.0.1"
            : (CONTEXT.JSONConfiguration.HostingEnvironment!.Value!.ToUpper().Equals("PUBLIC")
                ? "api.kongor.online"
                : CONTEXT.JSONConfiguration.HostingEnvironment!.Value);

    internal static async Task<string> GetServerAddress() =>
        CONTEXT.JSONConfiguration.HostingEnvironment!.Value!.ToUpper().Equals("LOCAL")
            ? "127.0.0.1"
            : (await new HttpClient().GetStringAsync("https://ipv4.icanhazip.com"))
                .Replace(@"\r", string.Empty).Replace(@"\n", string.Empty).Trim();

    internal static void HandleMasterServerPing()
    {
        if (CONTEXT.JSONConfiguration.HostingEnvironment!.Value!.ToUpper().Equals("LOCAL"))
        {
            Console.WriteLine("Master Server Was Not Pinged (Hosting Environment Is Local)");
        }

        else
        {
            IPStatus status = new Ping().Send(CONTEXT.MasterServerAddress).Status;

            if (status is not IPStatus.Success)
            {
                Console.WriteLine("Master Server Is Not Reachable");
                Console.ReadKey();
                Environment.Exit(1);
            }

            else { Console.WriteLine($"Master Server Ping Status: {status}"); }
        }
    }

    internal static string ServerNameWithWhiteSpaceCharacters(string desiredServerName)
    {
        // Technically, spaces are already allowed in "svr_name". The problem is that "-execute" / "Set" don't work the way we expect.
        // For example, "Set svr_name Best Server Ever!" will be properly tokenized and it will trigger LookupCVar("svr_name").Set("Best", "Server", "Ever!"); which will concatenate "Best" and "Server" but drop "Ever!" because ... reasons?
        // The last parameter is always ignored, which is why the configuration looks like the following: Set FooBar "1" "0".
        // To fix the issue, add a workaround parameter at the end. It doesn't matter what this is.
        const string workaroundParameter = "0";

        // Now "Set svr_name Best Server Ever! 0" will properly set svr_name to "Best Server Ever!".
        string updatedServerName = $"{desiredServerName} {workaroundParameter}";

        // This, sadly, only works on the Server Manager side, but when the Server Manager spawns a dedicated instance, we are back to "Set svr_name Best Server Ever!" which will, again, ignore the last part.
        // To fix the issue, we need to append ANOTHER workaround parameter. Excaping the spaces could work too.
        return $"{updatedServerName} {workaroundParameter}";
    }

    internal static void KillZombiesAndOrphansAndPreventDoppelgangers()
    {
        Process[] processes = Process.GetProcesses();

        if (processes.Count(process => process.ProcessName.ToUpper().Equals("COMPEL")) > 1)
        {
            Console.WriteLine("COMPEL Is Already Running");
            Environment.Exit(1);
        }

        Process[] serverManagerOrphans = processes.Where(process => process.ProcessName.ToUpper().Equals("HON_X64") && process.MainWindowTitle.Contains("K2 Server Manager")).ToArray();

        if (serverManagerOrphans.Any())
        {
            foreach (Process process in serverManagerOrphans)
                process.Kill();

            if (serverManagerOrphans.Length is 1)
                Console.WriteLine($"{serverManagerOrphans.Length} Orphaned Server Manager Process Has Been Killed");

            else
                Console.WriteLine($"{serverManagerOrphans.Length} Orphaned Server Manager Processes Have Been Killed");
        }

        Process[] serverOrphans = processes.Where(process => process.ProcessName.ToUpper().Equals("HON_X64") && process.MainWindowTitle.Contains(CONTEXT.JSONConfiguration.ServerNamePrefix!.Value!)).ToArray();

        if (serverOrphans.Any())
        {
            foreach (Process process in serverOrphans)
                process.Kill();

            if (serverOrphans.Length is 1)
                Console.WriteLine($"{serverOrphans.Length} Orphaned Server Process Has Been Killed");

            else
                Console.WriteLine($"{serverOrphans.Length} Orphaned Server Processes Have Been Killed");
        }

        Process[] proxyManagerZombies = processes.Where(process => process.ProcessName.ToUpper().Equals("PROXYMANAGER")).ToArray();

        if (proxyManagerZombies.Any())
        {
            foreach (Process process in proxyManagerZombies)
                process.Kill();

            if (proxyManagerZombies.Length is 1)
                Console.WriteLine($"{proxyManagerZombies.Length} Zombie Proxy Manager Process Has Been Killed");

            else
                Console.WriteLine($"{proxyManagerZombies.Length} Zombie Proxy Manager Processes Have Been Killed");
        }

        Process[] proxyZombies = processes.Where(process => process.ProcessName.ToUpper().Equals("PROXY")).ToArray();

        if (proxyZombies.Any())
        {
            foreach (Process process in proxyZombies)
                process.Kill();

            if (proxyZombies.Length is 1)
                Console.WriteLine($"{proxyZombies.Length} Zombie Proxy Process Has Been Killed");

            else
                Console.WriteLine($"{proxyZombies.Length} Zombie Proxy Processes Have Been Killed");
        }
    }

    internal static string HandleRuntimeArtefactsPath()
    {
        string runtimeArtefactsPath = CONTEXT.JSONConfiguration.RuntimeArtefactsPath!.Value!;

        string parsedRuntimeArtefactsPath = runtimeArtefactsPath.ToUpper() switch
        {
            "TEMP" => Path.GetTempPath(),
            "DOCUMENTS" => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            _ => Path.Combine(runtimeArtefactsPath)
        };

        return Path.Combine(parsedRuntimeArtefactsPath, "HON");
    }

    internal static async Task RunMaintenanceLoop()
    {
        Console.WriteLine();

        while (true) // TODO: Find A Good Condition Which Doesn't Create An Infinite Loop
        {
            await Task.Delay(15 * 60 * 1000); // 15 Minutes

            string[] files = Directory.GetFiles(HandleRuntimeArtefactsPath(), "*.honreplay", SearchOption.AllDirectories);

            foreach (string file in files)
            {
                if (File.GetLastWriteTime(file).AddMinutes(60) > DateTime.Now) continue;

                string directory = Path.Combine(Directory.GetParent(file)!.ToString(), Path.GetFileNameWithoutExtension(file).Replace("M", string.Empty));

                if (Directory.Exists(directory).Equals(false)) continue;

                try { Directory.Delete(directory, true); }
                catch (Exception exception) { Console.WriteLine(exception.Message); }
            }
        }
    }
}
