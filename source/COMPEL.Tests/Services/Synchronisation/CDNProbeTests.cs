namespace COMPEL.Tests.Services.Synchronisation;

/// <summary>
///     Verifies pre-flight content delivery network connectivity probing, including HTTP status handling and structured log output.
/// </summary>
public sealed class CDNProbeTests
{
    private static int GetAvailablePort()
    {
        using TcpListener listener = new (IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class TestHTTPServer : IAsyncDisposable
    {
        private readonly HttpListener listener = new ();
        private readonly int statusCode;
        private readonly Task processTask;
        private readonly CancellationTokenSource cancellation = new ();

        public string BaseURL { get; }

        public string? LastRequestMethod { get; private set; }

        public string? LastRequestURL { get; private set; }

        public string? LastUserAgent { get; private set; }

        public TestHTTPServer(int statusCode)
        {
            this.statusCode = statusCode;
            int port = GetAvailablePort();
            BaseURL = $"http://127.0.0.1:{port}/";
            listener.Prefixes.Add(BaseURL);
            listener.Start();
            processTask = Task.Run(Listen);
        }

        private async Task Listen()
        {
            while (cancellation.IsCancellationRequested is false)
            {
                try
                {
                    HttpListenerContext context = await listener.GetContextAsync();
                    LastRequestMethod = context.Request.HttpMethod;
                    LastRequestURL    = context.Request.RawUrl;
                    LastUserAgent     = context.Request.UserAgent;
                    context.Response.StatusCode = statusCode;
                    context.Response.Close();
                }

                catch (Exception)
                {
                    break;
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            cancellation.Cancel();
            listener.Stop();
            listener.Close();

            try
            {
                await processTask;
            }

            catch (Exception)
            {
            }

            cancellation.Dispose();
        }
    }

    private sealed class TestLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, exception)));
        }
    }

    [Test]
    public async Task Probe_Succeeds_When_CDN_Returns_200_OK()
    {
        await using TestHTTPServer server = new (statusCode: 200);

        CDNOptions options = new () { Host = server.BaseURL };
        TestLogger<DistributionSynchronisationService> logger = new ();
        DistributionSynchronisationService service = new (Options.Create(options), logger);

        bool result = await service.ProbeCDNConnectivity();

        using (Assert.Multiple())
        {
            await Assert.That(result).IsTrue();
            await Assert.That(server.LastRequestMethod).IsEqualTo("HEAD");
            await Assert.That(server.LastRequestURL).IsEqualTo($"/{service.Variant}/manifest.json");
            await Assert.That(server.LastUserAgent).IsEqualTo($"COMPEL/{VersionChecker.CurrentVersionDisplay}");
            await Assert.That(logger.Entries.Any(entry => entry.Level is LogLevel.Information && entry.Message.Contains("INIT: Probing CDN Connectivity At"))).IsTrue();
            await Assert.That(logger.Entries.Any(entry => entry.Level is LogLevel.Information && entry.Message.Contains("INIT: CDN Probe Succeeded (HTTP 200 OK)"))).IsTrue();
        }
    }

    [Test]
    public async Task Probe_Fails_When_CDN_Returns_Non_200_Status()
    {
        await using TestHTTPServer server = new (statusCode: 404);

        CDNOptions options = new () { Host = server.BaseURL };
        TestLogger<DistributionSynchronisationService> logger = new ();
        DistributionSynchronisationService service = new (Options.Create(options), logger);

        bool result = await service.ProbeCDNConnectivity();

        using (Assert.Multiple())
        {
            await Assert.That(result).IsFalse();
            await Assert.That(logger.Entries.Any(entry => entry.Level is LogLevel.Information && entry.Message.Contains("INIT: Probing CDN Connectivity At"))).IsTrue();
            await Assert.That(logger.Entries.Any(entry => entry.Level is LogLevel.Warning && entry.Message.Contains("WARN: CDN Probe Failed: HTTP 404"))).IsTrue();
        }
    }

    [Test]
    public async Task Probe_Fails_When_CDN_Is_Unreachable()
    {
        int closedPort = GetAvailablePort();
        CDNOptions options = new () { Host = $"http://127.0.0.1:{closedPort}/" };
        TestLogger<DistributionSynchronisationService> logger = new ();
        DistributionSynchronisationService service = new (Options.Create(options), logger);

        bool result = await service.ProbeCDNConnectivity();

        using (Assert.Multiple())
        {
            await Assert.That(result).IsFalse();
            await Assert.That(logger.Entries.Any(entry => entry.Level is LogLevel.Information && entry.Message.Contains("INIT: Probing CDN Connectivity At"))).IsTrue();
            await Assert.That(logger.Entries.Any(entry => entry.Level is LogLevel.Warning && entry.Message.Contains("WARN: CDN Probe Failed:"))).IsTrue();
        }
    }
}
