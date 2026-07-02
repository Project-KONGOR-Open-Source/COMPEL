namespace COMPEL.Services.Ping;

/// <summary>
///     Reports the ping responder's bind state via "/health": healthy once bound, unhealthy if the bind failed, and degraded while it has not yet bound, so a responder that never comes up (for example because the distribution never becomes ready) is visible rather than being reported as healthy indefinitely.
/// </summary>
internal sealed class UDPPingResponderHealthCheck(UDPPingResponder responder) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        HealthCheckResult result = responder.IsBound switch
        {
            true  => HealthCheckResult.Healthy(),
            false => HealthCheckResult.Unhealthy("The UDP Ping Responder Failed To Bind Its Port"),
            null  => HealthCheckResult.Degraded("The UDP Ping Responder Has Not Yet Bound Its Port")
        };

        return Task.FromResult(result);
    }
}
