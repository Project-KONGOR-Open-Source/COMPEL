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
