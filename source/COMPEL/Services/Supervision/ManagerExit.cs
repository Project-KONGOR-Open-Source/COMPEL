namespace COMPEL.Services.Supervision;

/// <summary>
///     Describes the exit codes of the match server manager that COMPEL knows the meaning of.
/// </summary>
public static class ManagerExit
{
    /// <summary>
    ///     The exit code with which the match server manager reports that it could not open its own port.
    /// </summary>
    public const int PortUnavailable = 98;

    public static string Describe(int exitCode, PortPlan ports)
        => exitCode is PortUnavailable
            ? $"The Match Server Manager Could Not Open Its Port {ports.ManagerPort}, So Another Process Is Probably Holding It"
            : $"The Match Server Manager Exited With Code {exitCode}";
}
