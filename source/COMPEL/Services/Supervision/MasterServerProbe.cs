namespace COMPEL.Services.Supervision;

/// <summary>
///     Checks that the master server answers, the way the launcher probes it: a HEAD request to its health endpoint over plain HTTP, which is how the manager talks to it.
/// </summary>
public static class MasterServerProbe
{
    /// <summary>
    ///     The health address of the given master server, which is a host name or an IP address with an optional port.
    /// </summary>
    public static Uri HealthAddress(string masterServer) => new ($"http://{masterServer}/health");

    /// <summary>
    ///     Returns <see langword="true"/> when the master server answers a HEAD request to its health address with a success status, and <see langword="false"/> otherwise, including when it cannot be reached in time.
    /// </summary>
    public static async Task<bool> IsReachable(HttpClient client, string masterServer, CancellationToken cancellationToken)
    {
        try
        {
            using HttpRequestMessage request = new (HttpMethod.Head, HealthAddress(masterServer));
            using HttpResponseMessage response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);

            return response.IsSuccessStatusCode;
        }

        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }
}
