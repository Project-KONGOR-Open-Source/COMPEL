namespace COMPEL;

[Serializable]
public class CONFIGURATION
{
    [JsonPropertyName("UserName")]
    public UserName? UserName { get; set; }

    [JsonPropertyName("Password")]
    public Password? Password { get; set; }

    [JsonPropertyName("Instances")]
    public Instances? Instances { get; set; }

    [JsonPropertyName("HostingEnvironment")]
    public HostingEnvironment? HostingEnvironment { get; set; }

    [JsonPropertyName("Location")]
    public Location? Location { get; set; } // TODO: Remove And Set Programatically

    [JsonPropertyName("ServerNamePrefix")]
    public ServerNamePrefix? ServerNamePrefix { get; set; }

    [JsonPropertyName("UseProxy")]
    public UseProxy? UseProxy { get; set; }

    [JsonPropertyName("PortRangeOffset")]
    public PortRangeOffset? PortRangeOffset { get; set; }

    [JsonPropertyName("RuntimeArtefactsPath")]
    public RuntimeArtefactsPath? RuntimeArtefactsPath { get; set; }
}

public class UserName
{
    [JsonPropertyName("Value")]
    public string? Value { get; set; }

    [JsonPropertyName("Description")]
    public string Description => "The name of the user which will host the game servers. This needs to match the name of a registered Project KONGOR user.";
}

public class Password
{
    [JsonPropertyName("Value")]
    public string? Value { get; set; }

    [JsonPropertyName("Description")]
    public string Description => "The password of the user which will host the game servers. This needs to match the password of the registered Project KONGOR user set to host the game servers.";
}

public class Instances
{
    [JsonPropertyName("Value")]
    public int? Value { get; set; }

    [JsonPropertyName("Description")]
    public string Description => "The number of server instances to spawn. Instances are spawned with the process priority set to real-time if launching COMPEL with elevated privileges, or to high priority if not.";
}

public class HostingEnvironment
{
    [JsonPropertyName("Value")]
    public string? Value { get; set; }

    [JsonPropertyName("Description")]
    public string Description => "Whether to connect to a 'local' or a 'public' master server. Always use 'public', unless you are hosting Project KONGOR locally.";
}

public class Location
{
    [JsonPropertyName("Value")]
    public string? Value { get; set; }

    [JsonPropertyName("Description")]
    public string Description => "Normally, the location can be set to any value, but, in order for the server to be TMM-compatible, only the following values are valid: 'USW', 'USE', 'EU', 'AU', 'BR', 'RU', and 'SEA'.";
}

public class ServerNamePrefix
{
    [JsonPropertyName("Value")]
    public string? Value { get; set; }

    [JsonPropertyName("Description")]
    public string Description => "The base name of the game server instances. The name of each server instance will be the concatenation of this base name and the 1-based index of the instance.";
}

public class UseProxy
{
    [JsonPropertyName("Value")]
    public bool? Value { get; set; }

    [JsonPropertyName("Description")]
    public string Description => "The proxy is included in the server distribution from the CDN, and acts as an anti-cheat and anti-DDoS layer. If you're hosting via this distribution, use 'true' to host via proxy, otherwise, use 'false'.";
}

public class PortRangeOffset
{
    [JsonPropertyName("Value")]
    public int Value { get; set; }

    [JsonPropertyName("Description")]
    public string Description => "The offset from the start of the valid game/voice port ranges at which the game/voice ports to be used at runtime will start. The game/voice port ranges without the proxy are 11235-11335/11435-11535, and with the proxy they are 21235-21335/21435-21535.";
}

public class RuntimeArtefactsPath
{
    [JsonPropertyName("Value")]
    public string? Value { get; set; }

    [JsonPropertyName("Description")]
    public string Description => "The directory that the 'HON' folder which contains the runtime artefacts (e.g. replays, logs) will be saved into. This value can either be a fully qualified path, or one of the following aliases: 'TEMP', 'DOCUMENTS'.";
}
