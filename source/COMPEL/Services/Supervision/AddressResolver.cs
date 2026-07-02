namespace COMPEL.Services.Supervision;

/// <summary>
///     Resolves the server's own advertised address and the master server endpoint from the configured gateway, reproducing the legacy COMPEL's address logic.
///     The public master server is the Project KONGOR gateway; a gateway of localhost routes to a local master server, which is how the manager is pointed at a local NEXUS instance.
/// </summary>
public sealed class AddressResolver
{
    private const string PublicMasterServerAddress = "api.kongor.net";
    private const string LocalMasterServerAddress  = "127.0.0.1";
    private const int    LocalMasterServerPort     = 5555;

    private static readonly string[] PublicIPServices =
    [
        "https://ipv4.icanhazip.com",
        "https://api.ipify.org",
        "https://ifconfig.me/ip",
        "https://checkip.amazonaws.com"
    ];

    private readonly MatchServerManagerOptions manager;
    private readonly ILogger<AddressResolver> logger;

    private string? cachedServerAddress;

    public AddressResolver(IOptions<MatchServerManagerOptions> manager, ILogger<AddressResolver> logger)
    {
        this.manager = manager.Value;
        this.logger = logger;
    }

    private bool GatewayIsLocalhost => manager.Gateway.ToUpperInvariant() is "LOCALHOST" or "127.0.0.1";

    /// <summary>
    ///     Resolves the server's own advertised IPv4 address. The result is cached for the lifetime of the process.
    /// </summary>
    public async Task<string> ResolveServerAddress(CancellationToken cancellationToken = default)
    {
        if (cachedServerAddress is not null)
            return cachedServerAddress;

        string gateway = manager.Gateway;

        string resolved;

        if (gateway.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            resolved = "127.0.0.1";

        else if (gateway.Equals("PUBLIC", StringComparison.OrdinalIgnoreCase))
            resolved = await DetectPublicIPAddress(cancellationToken).ConfigureAwait(false);

        else if (IPAddress.TryParse(gateway, out IPAddress? parsed))
        {
            // "MapToIPv4" Silently Reinterprets Any IPv6 Address's Low-Order Bits Rather Than Throwing, So A Genuine (Non-Mapped) IPv6 Literal Must Be Rejected Explicitly Here.
            if (parsed.AddressFamily is AddressFamily.InterNetworkV6 && parsed.IsIPv4MappedToIPv6 is false)
                throw new InvalidOperationException($@"Gateway ""{gateway}"" Is An IPv6 Address; COMPEL Requires An IPv4 Address");

            resolved = parsed.MapToIPv4().ToString();
        }

        else
        {
            IPAddress[] addresses = await Dns.GetHostAddressesAsync(gateway, cancellationToken).ConfigureAwait(false);

            IPAddress? ipv4Address = addresses.FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork);

            resolved = ipv4Address?.ToString() ?? throw new InvalidOperationException($@"Unable To Resolve Gateway ""{gateway}"" To An IPv4 Address");
        }

        cachedServerAddress = resolved;

        return resolved;
    }

    /// <summary>
    ///     The master server host and port passed to the manager via the "-masterserver" argument. The public gateway takes no explicit port; the local gateway uses the local NEXUS port.
    /// </summary>
    public string MasterServerHostAndPort => GatewayIsLocalhost ? $"{LocalMasterServerAddress}:{LocalMasterServerPort}" : PublicMasterServerAddress;

    private async Task<string> DetectPublicIPAddress(CancellationToken cancellationToken)
    {
        using HttpClient httpClient = new () { Timeout = TimeSpan.FromSeconds(5) };

        foreach (string service in PublicIPServices)
        {
            try
            {
                string response = await httpClient.GetStringAsync(service, cancellationToken).ConfigureAwait(false);

                string ipAddress = response.Replace("\r", string.Empty).Replace("\n", string.Empty).Trim();

                if (IPAddress.TryParse(ipAddress, out _))
                    return ipAddress;
            }

            catch (Exception exception)
            {
                logger.LogDebug(exception, "Public IP Detection Service {Service} Failed", service);
            }
        }

        throw new InvalidOperationException("Unable To Detect Public IP Address From Any Service");
    }
}
