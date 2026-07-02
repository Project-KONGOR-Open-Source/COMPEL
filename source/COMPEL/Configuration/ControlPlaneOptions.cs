namespace COMPEL.Configuration;

/// <summary>
///     Configures the HTTP control plane that NEXUS and host operators use to query and manage this match server manager.
/// </summary>
public sealed class ControlPlaneOptions
{
    /// <summary>
    ///     The shared bearer token required to call the management endpoints. When empty, the management endpoints reject every request, so a token must be configured before remote management can be used.
    /// </summary>
    public string AuthenticationToken { get; set; } = string.Empty;
}
