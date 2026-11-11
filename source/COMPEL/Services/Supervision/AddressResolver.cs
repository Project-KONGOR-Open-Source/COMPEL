namespace COMPEL.Services.Supervision;

/// <summary>
///     Resolves the server's own advertised address from the configured gateway, and gives the configured master server endpoint.
/// </summary>
public sealed class AddressResolver
{
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
            // "MapToIPv4" Silently Reinterprets Any IPv6 Address's Low-Order Bits Rather Than Throwing, So A Genuine (Non-Mapped) IPv6 Literal Must Be Rejected Explicitly Here
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
    ///     The master server host and port passed to the manager via the "-masterserver" and "-ascensionserver" arguments.
    /// </summary>
    public string MasterServerHostAndPort => manager.MasterServer;

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
