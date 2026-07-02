namespace COMPEL.Services.Proxy;

/// <summary>
///     Blocks source addresses using the Windows firewall, faithfully reproducing the legacy proxy manager's "HoN DDoS IP Block" rule.
///     Requires elevated privileges; the firewall controller selection falls back to <see cref="NoOperationFirewallController"/> when the process is not elevated.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsFirewallController : IFirewallController
{
    private const string RuleName = "HoN DDoS IP Block";

    private readonly ILogger<WindowsFirewallController> logger;

    public WindowsFirewallController(ILogger<WindowsFirewallController> logger) => this.logger = logger;

    public void BlockAddress(IPAddress address)
        => RunNetsh($@"advfirewall firewall add rule name=""{RuleName}"" dir=in action=block remoteip={address}");

    public void ClearBlockedAddresses()
        => RunNetsh($@"advfirewall firewall delete rule name=""{RuleName}""");

    private void RunNetsh(string arguments)
    {
        try
        {
            ProcessStartInfo startInfo = new ("netsh", arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using Process? process = Process.Start(startInfo);

            process?.WaitForExit(10000);
        }

        catch (Exception exception)
        {
            logger.LogWarning(exception, "Firewall Command Failed: netsh {Arguments}", arguments);
        }
    }
}
