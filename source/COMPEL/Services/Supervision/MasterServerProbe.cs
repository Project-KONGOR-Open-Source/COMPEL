namespace COMPEL.Services.Supervision;

/// <summary>
///     Checks that the master server answers, the way the launcher probes it: a HEAD request to its health endpoint over plain HTTP, which is how the manager talks to it.
/// </summary>
public static class MasterServerProbe
{
    /// <summary>
    ///     The wait after the first failed attempt.
    /// </summary>
    public static readonly TimeSpan FirstRetryDelay = TimeSpan.FromSeconds(1);

    /// <summary>
    ///     The longest wait between two attempts, which the waits grow towards.
    /// </summary>
    public static readonly TimeSpan LongestRetryDelay = TimeSpan.FromSeconds(30);

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

    /// <summary>
    ///     The wait after the given failed attempt, counted from zero: the first wait doubled once per attempt, and never longer than the longest wait.
    /// </summary>
    public static TimeSpan RetryDelay(int attempt)
    {
        TimeSpan delay = FirstRetryDelay;

        for (int doubling = 0; doubling < attempt && delay < LongestRetryDelay; doubling++)
            delay *= 2;

        return delay < LongestRetryDelay ? delay : LongestRetryDelay;
    }

    /// <summary>
    ///     Waits until the master server answers, with no overall limit, and returns <see langword="true"/> then.
    ///     Returns <see langword="false"/>, without throwing, once the cancellation token is cancelled, whether during a request or during a wait.
    /// </summary>
    public static async Task<bool> WaitUntilReachable(HttpClient client, string masterServer, Func<TimeSpan, CancellationToken, Task> delay, Action<int, TimeSpan> reportWait, CancellationToken cancellationToken)
    {
        for (int attempt = 0; ; attempt++)
        {
            if (await IsReachable(client, masterServer, cancellationToken).ConfigureAwait(false))
                return true;

            // A Cancelled Request Is Reported As Unreachable, So The Token Is Checked Before Waiting Again
            if (cancellationToken.IsCancellationRequested)
                return false;

            TimeSpan retryDelay = RetryDelay(attempt);

            reportWait(attempt, retryDelay);

            try
            {
                await delay(retryDelay, cancellationToken).ConfigureAwait(false);
            }

            catch (OperationCanceledException)
            {
                return false;
            }
        }
    }
}
