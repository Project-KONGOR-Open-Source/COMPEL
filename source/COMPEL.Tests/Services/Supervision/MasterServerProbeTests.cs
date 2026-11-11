namespace COMPEL.Tests.Services.Supervision;

/// <summary>
///     Verifies the master server's health address and the start-up reachability check, using a stub message handler instead of the network.
/// </summary>
public sealed class MasterServerProbeTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;

            return Task.FromResult(respond(request));
        }
    }

    [Test]
    public async Task The_Health_Address_Is_Plain_HTTP()
    {
        await Assert.That(MasterServerProbe.HealthAddress("localhost:5555").ToString()).IsEqualTo("http://localhost:5555/health");
    }

    [Test]
    public async Task A_Successful_Answer_Is_Reachable()
    {
        StubHandler handler = new (_ => new HttpResponseMessage(HttpStatusCode.OK));

        using HttpClient client = new (handler);

        bool reachable = await MasterServerProbe.IsReachable(client, "localhost:5555", CancellationToken.None);

        using (Assert.Multiple())
        {
            await Assert.That(reachable).IsTrue();
            await Assert.That(handler.Request!.Method).IsEqualTo(HttpMethod.Head);
            await Assert.That(handler.Request!.RequestUri!.ToString()).IsEqualTo("http://localhost:5555/health");
        }
    }

    [Test]
    public async Task An_Unreachable_Master_Server_Is_Reported()
    {
        using HttpClient unavailable = new (new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        using HttpClient throwing = new (new StubHandler(_ => throw new HttpRequestException("Connection Refused")));

        using (Assert.Multiple())
        {
            await Assert.That(await MasterServerProbe.IsReachable(unavailable, "localhost:5555", CancellationToken.None)).IsFalse();
            await Assert.That(await MasterServerProbe.IsReachable(throwing, "localhost:5555", CancellationToken.None)).IsFalse();
        }
    }
}
