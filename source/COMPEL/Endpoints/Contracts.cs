namespace COMPEL.Endpoints;

/// <summary>
///     The response to a latency probe against the control plane.
/// </summary>
public sealed record PingResponse(string Application, string Version, long ServerTimeUnixMilliseconds);

/// <summary>
///     The game, voice, and ping ports the manager has been allocated. Local ports are what the servers bind; public ports are what clients connect to (equal to the local ports unless the proxy is enabled).
/// </summary>
public sealed record PortAllocationResponse
(
    int GameStart, int GameEnd,
    int VoiceStart, int VoiceEnd,
    int PublicGameStart, int PublicGameEnd,
    int PublicVoiceStart, int PublicVoiceEnd,
    int PingPort
);

/// <summary>
///     A snapshot of the match server manager's configuration and runtime state.
/// </summary>
public sealed record StatusResponse
(
    string Application,
    string Version,
    string ServerNamePrefix,
    string Location,
    string? ServerAddress,
    int Instances,
    bool UseProxy,
    int PortRangeOffset,
    PortAllocationResponse Ports,
    string? DistributionVersion,
    string SynchronisationState,
    bool ManagerRunning,
    bool ProxyRunning,
    bool? PingResponderBound,
    int ProxyFailedForwarderCount,
    double UptimeSeconds
);

/// <summary>
///     The outcome of a lifecycle action (synchronise, start, stop, or restart) requested against the control plane.
/// </summary>
public sealed record ActionResponse(string Action, bool Accepted, string Detail);
