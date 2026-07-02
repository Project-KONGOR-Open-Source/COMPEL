namespace COMPEL.Configuration;

/// <summary>
///     The on-disk representation of "COMPEL.json": a single, self-describing configuration file in the format used by the legacy COMPEL.
///     Each setting carries its <c>Value</c> and a human-readable <c>Description</c>. The descriptions are written to the file when it is generated and ignored when it is read back, so they document the file without affecting how it binds.
/// </summary>
public sealed class CompelConfigurationFile
{
    public UserNameSetting UserName { get; set; } = new ();
    public PasswordSetting Password { get; set; } = new ();
    public InstancesSetting Instances { get; set; } = new ();
    public GatewaySetting Gateway { get; set; } = new ();
    public LocationSetting Location { get; set; } = new ();
    public ServerNamePrefixSetting ServerNamePrefix { get; set; } = new ();
    public UseProxySetting UseProxy { get; set; } = new ();
    public PortRangeOffsetSetting PortRangeOffset { get; set; } = new ();
    public RuntimeArtefactsPathSetting RuntimeArtefactsPath { get; set; } = new ();
    public CDNSynchronisationSetting CDNSynchronisation { get; set; } = new ();
    public AuthenticationTokenSetting AuthenticationToken { get; set; } = new ();
    public ControlPlanePortSetting ControlPlanePort { get; set; } = new ();
}

public sealed class UserNameSetting
{
    public string Value { get; set; } = "USERNAME";
    public string Description => "The name of the user which will host the game servers. This needs to match the name of a registered Project KONGOR user.";
}

public sealed class PasswordSetting
{
    public string Value { get; set; } = "PASSWORD";
    public string Description => "The password of the user which will host the game servers. This needs to match the password of the registered Project KONGOR user set to host the game servers.";
}

public sealed class InstancesSetting
{
    public int Value { get; set; } = 1;
    public string Description => "The number of server instances to spawn. This must be between one and the number of logical processors. The server manager spreads the instances across the available processors; running COMPEL with elevated privileges is required for the manager to assign their processor affinity.";
}

public sealed class GatewaySetting
{
    public string Value { get; set; } = "kongor.net";
    public string Description => "The entry point for game servers and the server manager. Use 'kongor.net' for the official public gateway, 'localhost' for local development, a LAN or public IP address, or a local or public host name to resolve.";
}

public sealed class LocationSetting
{
    public string Value { get; set; } = "EU";
    public string Description => "Normally, the location can be set to any value, but, in order for the server to be TMM-compatible, only the following values are valid: 'USW', 'USE', 'EU', 'AU', 'BR', 'RU', and 'SEA'.";
}

public sealed class ServerNamePrefixSetting
{
    public string Value { get; set; } = "KONGOR ARENA";
    public string Description => "The base name of the game server instances. The name of each server instance will be the concatenation of this base name and the 1-based index of the instance.";
}

public sealed class UseProxySetting
{
    public bool Value { get; set; }
    public string Description => "Whether to run COMPEL's built-in anti-cheat and anti-DDoS proxy in front of the game servers. When enabled, clients connect to public ports offset 10000 above the local server ports (e.g. 21235 instead of 11235) and the proxy forwards them to the servers. Defaults to 'false': with the proxy disabled the local server ports are public, which is the fully supported path. The proxy transport is implemented, but the client challenge protocol required on the public port range is not yet, so only enable it once that has been added.";
}

public sealed class PortRangeOffsetSetting
{
    public int Value { get; set; }
    public string Description => "The offset from the start of the valid game/voice port ranges at which the game/voice ports to be used at runtime will start. The game/voice port ranges without the proxy are 11235-11335/11435-11535, and with the proxy they are 21235-21335/21435-21535.";
}

public sealed class RuntimeArtefactsPathSetting
{
    public string Value { get; set; } = "DEFAULT";
    public string Description => "The base directory beneath which the match server writes its runtime artefacts (e.g. replays, logs). This value is either the 'DEFAULT' alias, which places artefacts beneath the host account's profile (its 'Documents/Heroes of Newerth x64' tree, where everything else is written), or a fully qualified path to use as the base profile directory instead. This setting applies on Windows only; the Linux server build writes to a fixed location ('/opt/hon/config') and ignores it.";
}

public sealed class CDNSynchronisationSetting
{
    public bool Value { get; set; } = true;
    public string Description => "Whether to synchronise the match server distribution from the CDN on startup. Set to 'false' to skip the initial synchronisation for development and testing, in which case the existing local distribution is used; the '/sync' management endpoint can still trigger a synchronisation on demand.";
}

public sealed class AuthenticationTokenSetting
{
    public string Value { get; set; } = string.Empty;
    public string Description => "The bearer token that NEXUS and host operators must present to use the remote management endpoints (status, synchronisation, and instance lifecycle). Leave empty to disable remote management.";
}

public sealed class ControlPlanePortSetting
{
    public int Value { get; set; } = 8080;
    public string Description => "The TCP port on which the HTTP control plane (the latency ping, the health checks, and the management endpoints) listens.";
}
