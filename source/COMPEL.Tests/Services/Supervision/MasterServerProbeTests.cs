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
            await Assert.That(handler.Request?.Method).IsEqualTo(HttpMethod.Head);
            await Assert.That(handler.Request?.RequestUri?.ToString()).IsEqualTo("http://localhost:5555/health");
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

    private sealed class QueueHandler(Queue<Func<CancellationToken, HttpResponseMessage>> answers) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;

            return Task.FromResult(answers.Dequeue()(cancellationToken));
        }
    }

    private static Func<CancellationToken, HttpResponseMessage> Answer(HttpStatusCode statusCode) => _ => new HttpResponseMessage(statusCode);

    private static Func<CancellationToken, HttpResponseMessage> Failure() => _ => throw new HttpRequestException("Connection Refused");

    [Test]
    public async Task The_Retry_Delay_Doubles_Up_To_Its_Limit()
    {
        int[] seconds = [.. Enumerable.Range(0, 7).Select(attempt => (int) MasterServerProbe.RetryDelay(attempt).TotalSeconds)];

        await Assert.That(seconds).IsEquivalentTo(new[] { 1, 2, 4, 8, 16, 30, 30 });
    }

    [Test]
    public async Task A_Late_Attempt_Stays_At_The_Limit()
    {
        await Assert.That(MasterServerProbe.RetryDelay(int.MaxValue)).IsEqualTo(TimeSpan.FromSeconds(30));
    }

    [Test]
    public async Task A_Reachable_Master_Server_Is_Not_Waited_For()
    {
        QueueHandler handler = new (new ([Answer(HttpStatusCode.OK)]));
        List<TimeSpan> delays = [];
        List<(int Attempt, TimeSpan Delay)> waits = [];

        using HttpClient client = new (handler);

        bool reachable = await MasterServerProbe.WaitUntilReachable(client, "localhost:5555", (delay, _) => { delays.Add(delay); return Task.CompletedTask; }, (attempt, delay) => waits.Add((attempt, delay)), CancellationToken.None);

        using (Assert.Multiple())
        {
            await Assert.That(reachable).IsTrue();
            await Assert.That(handler.RequestCount).IsEqualTo(1);
            await Assert.That(waits).IsEmpty();
            await Assert.That(delays).IsEmpty();
        }
    }

    [Test]
    public async Task The_Wait_Ends_Once_The_Master_Server_Answers()
    {
        QueueHandler handler = new (new ([Answer(HttpStatusCode.ServiceUnavailable), Failure(), Answer(HttpStatusCode.OK)]));
        List<TimeSpan> delays = [];
        List<(int Attempt, TimeSpan Delay)> waits = [];

        using HttpClient client = new (handler);

        bool reachable = await MasterServerProbe.WaitUntilReachable(client, "localhost:5555", (delay, _) => { delays.Add(delay); return Task.CompletedTask; }, (attempt, delay) => waits.Add((attempt, delay)), CancellationToken.None);

        using (Assert.Multiple())
        {
            await Assert.That(reachable).IsTrue();
            await Assert.That(handler.RequestCount).IsEqualTo(3);
            await Assert.That(waits).IsEquivalentTo(new[] { (0, TimeSpan.FromSeconds(1)), (1, TimeSpan.FromSeconds(2)) });
            await Assert.That(delays).IsEquivalentTo(new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2) });
        }
    }

    [Test]
    public async Task The_Wait_Has_No_Overall_Timeout()
    {
        Queue<Func<CancellationToken, HttpResponseMessage>> answers = new (Enumerable.Repeat(Answer(HttpStatusCode.ServiceUnavailable), 40));
        answers.Enqueue(Answer(HttpStatusCode.OK));

        QueueHandler handler = new (answers);
        List<TimeSpan> delays = [];

        using HttpClient client = new (handler);

        bool reachable = await MasterServerProbe.WaitUntilReachable(client, "localhost:5555", (delay, _) => { delays.Add(delay); return Task.CompletedTask; }, (_, _) => { }, CancellationToken.None);

        using (Assert.Multiple())
        {
            await Assert.That(reachable).IsTrue();
            await Assert.That(handler.RequestCount).IsEqualTo(41);
            await Assert.That(delays.Max()).IsEqualTo(TimeSpan.FromSeconds(30));
            await Assert.That(delays.TakeLast(35).All(delay => delay == TimeSpan.FromSeconds(30))).IsTrue();
        }
    }

    [Test]
    public async Task Cancelling_During_A_Delay_Ends_The_Wait()
    {
        QueueHandler handler = new (new (Enumerable.Repeat(Answer(HttpStatusCode.ServiceUnavailable), 10)));

        using CancellationTokenSource source = new ();
        using HttpClient client = new (handler);

        int delayCalls = 0;

        bool reachable = await MasterServerProbe.WaitUntilReachable(client, "localhost:5555", (_, _) =>
        {
            if (++delayCalls is 2)
            {
                source.Cancel();

                return Task.FromCanceled(source.Token);
            }

            return Task.CompletedTask;
        }, (_, _) => { }, source.Token);

        using (Assert.Multiple())
        {
            await Assert.That(reachable).IsFalse();
            await Assert.That(handler.RequestCount).IsEqualTo(2);
        }
    }

    [Test]
    public async Task Cancelling_During_A_Request_Ends_The_Wait()
    {
        using CancellationTokenSource source = new ();

        QueueHandler handler = new (new ([_ =>
        {
            source.Cancel();

            throw new TaskCanceledException();
        }]));

        using HttpClient client = new (handler);

        int delayCalls = 0;
        int waitCalls = 0;

        bool reachable = await MasterServerProbe.WaitUntilReachable(client, "localhost:5555", (_, _) => { delayCalls++; return Task.CompletedTask; }, (_, _) => waitCalls++, source.Token);

        using (Assert.Multiple())
        {
            await Assert.That(reachable).IsFalse();
            await Assert.That(waitCalls).IsEqualTo(0);
            await Assert.That(delayCalls).IsEqualTo(0);
        }
    }
}
