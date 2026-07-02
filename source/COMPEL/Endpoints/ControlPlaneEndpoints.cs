namespace COMPEL.Endpoints;

/// <summary>
///     Maps the HTTP control plane: an anonymous latency probe plus the bearer-token-protected management endpoints that NEXUS and host operators use to query status and drive lifecycle actions.
/// </summary>
public static class ControlPlaneEndpoints
{
    public static WebApplication MapControlPlaneEndpoints(this WebApplication application)
    {
        long startTicks = Environment.TickCount64;

        // Anonymous Latency Probe.
        application.MapGet("/ping", () => TypedResults.Ok(new PingResponse("COMPEL", GeneratedVersionInformation.VersionString, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())));

        RouteGroupBuilder management = application.MapGroup(string.Empty);

        management.AddEndpointFilter(async (context, next) =>
        {
            IResult? failure = ControlPlaneAuthentication.Validate(context.HttpContext);

            return failure is not null ? failure : await next(context);
        });

        management.MapGet("/status", (MatchServerManagerSupervisor supervisor, DistributionSynchronisationService distribution, UDPProxyService proxy, UDPPingResponder pingResponder, IOptions<MatchServerManagerOptions> optionsAccessor) =>
        {
            MatchServerManagerOptions options = optionsAccessor.Value;
            PortPlan ports = supervisor.Ports;

            PortAllocationResponse portAllocation = new
            (
                GameStart:        ports.LocalGameStart,
                GameEnd:          ports.LocalGameEnd,
                VoiceStart:       ports.LocalVoiceStart,
                VoiceEnd:         ports.LocalVoiceEnd,
                PublicGameStart:  ports.PublicGameStart,
                PublicGameEnd:    ports.PublicGameEnd,
                PublicVoiceStart: ports.PublicVoiceStart,
                PublicVoiceEnd:   ports.PublicVoiceEnd,
                PingPort:         ports.PingPort
            );

            StatusResponse status = new
            (
                Application:               "COMPEL",
                Version:                   GeneratedVersionInformation.VersionString,
                ServerNamePrefix:          options.ServerNamePrefix,
                Location:                  options.Location,
                ServerAddress:             supervisor.ServerAddress,
                Instances:                 options.Instances,
                UseProxy:                  options.UseProxy,
                PortRangeOffset:           options.PortRangeOffset,
                Ports:                     portAllocation,
                DistributionVersion:       distribution.DistributionVersion,
                SynchronisationState:      distribution.SynchronisationState,
                ManagerRunning:            supervisor.IsRunning,
                ProxyRunning:              proxy.IsRunning,
                PingResponderBound:        pingResponder.IsBound,
                ProxyFailedForwarderCount: proxy.FailedForwarderCount,
                UptimeSeconds:             (Environment.TickCount64 - startTicks) / 1000.0
            );

            return TypedResults.Ok(status);
        });

        management.MapPost("/sync", IResult (DistributionSynchronisationService distribution, MatchServerManagerSupervisor supervisor, IHostApplicationLifetime lifetime) =>
        {
            // Synchronising Rewrites The Installation Directory The Manager Runs From. Doing So While The Manager Is Running Would Delete Or Overwrite Files The Live Servers Hold Open (A Sharing Violation On Windows, A Replaced Inode On Linux), So The Manager Must Be Stopped First. Checking The Desired State (Not Just The Live State) Also Rejects The Request When The Manager Has Merely Crashed And The Supervisor Is About To Relaunch It.
            if (supervisor.IsRunning || supervisor.DesiredRunning)
                return TypedResults.Conflict(new ActionResponse("sync", false, @"The Match Server Manager Must Be Stopped Via ""/instances/stop"" Before Synchronising"));

            // Synchronisation Can Take A While, So It Runs In The Background; The Result Is Observable Via "/status".
            _ = Task.Run(async () =>
            {
                try { await distribution.SynchroniseNow(lifetime.ApplicationStopping).ConfigureAwait(false); }
                catch { /* The Outcome Is Recorded On The Service's State And The Failure Is Logged Within. */ }
            });

            return TypedResults.Ok(new ActionResponse("sync", true, "Synchronisation Started"));
        });

        management.MapPost("/instances/start", (MatchServerManagerSupervisor supervisor) =>
        {
            supervisor.RequestStart();

            return TypedResults.Ok(new ActionResponse("start", true, "Start Requested"));
        });

        management.MapPost("/instances/stop", (MatchServerManagerSupervisor supervisor) =>
        {
            supervisor.RequestStop();

            return TypedResults.Ok(new ActionResponse("stop", true, "Stop Requested"));
        });

        management.MapPost("/instances/restart", async (MatchServerManagerSupervisor supervisor, CancellationToken cancellationToken) =>
        {
            await supervisor.RequestRestart(cancellationToken);

            return TypedResults.Ok(new ActionResponse("restart", true, "Restart Requested"));
        });

        return application;
    }
}
