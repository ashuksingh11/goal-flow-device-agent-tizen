using GoalFlow.Device.Contracts;
using GoalFlow.Device.Modules.Steering;
using GoalFlow.Device.Transport;
using Microsoft.Extensions.Logging;
using Tizen.Applications;

namespace GoalFlow.Device;

/// <summary>
/// Headless Tizen service host for the v2 GoalFlow device agent. This class owns
/// ONLY platform lifecycle + transport; the portable SK agent (DI container,
/// capability plugins, steering modules) is built by <see cref="DeviceHost"/> and
/// runs unchanged from the Ubuntu build.
///
/// <see cref="ServiceApplication.OnCreate"/> must return promptly, so the
/// long-running connect/receive loop runs on a background task; the WebSocket
/// transport (<see cref="WsClient"/>) owns connect-retry + reconnect-on-drop.
/// </summary>
public sealed class GoalFlowService : ServiceApplication
{
    private CancellationTokenSource? _cts;
    private DeviceHost? _host;
    private Task? _connectLoop;

    protected override void OnCreate()
    {
        base.OnCreate();

        // Log the lifecycle DIRECTLY via dlog (not the ILogger pipeline) so there
        // is ALWAYS visible output under `dlogutil GOALFLOW`, even if host build
        // throws (e.g. an assembly-load failure) before the logger logs anything.
        Tizen.Log.Info(DlogLoggerProvider.Tag, "OnCreate: starting GoalFlow device service");
        try
        {
            // Env-free config (Tizen services aren't launched with the shell env) —
            // reads a bundled goalflow.conf; see DeviceConfig.
            var config = DeviceConfig.Load();
            var dataDir = config.ResolveDataDir();
            _host = DeviceHost.Build(config, dataDir);

            var wsUrl = config.Get("WS_URL", "ws://localhost:8000/ws");
            Tizen.Log.Info(DlogLoggerProvider.Tag, $"OnCreate: host built, connecting to {wsUrl}");
            _cts = new CancellationTokenSource();
            _connectLoop = Task.Run(() => RunAsync(new Uri(wsUrl), _cts.Token));
        }
        catch (Exception ex)
        {
            // An unhandled throw here would terminate the service silently (no
            // GOALFLOW log line). Surface it to dlog so `dlogutil GOALFLOW` shows why.
            Tizen.Log.Error(DlogLoggerProvider.Tag, $"OnCreate FAILED: {ex}");
            throw;
        }
    }

    /// <summary>
    /// Connect to the cloud hub and pump frames until cancellation. Each inbound
    /// frame is handled on a BACKGROUND task so the receive loop keeps answering
    /// pings while a plan is being built (30-60s of LLM calls) — blocking the loop
    /// lets the cloud's keepalive close the socket mid-plan.
    /// </summary>
    private async Task RunAsync(Uri url, CancellationToken ct)
    {
        var host = _host!;
        var loggerFactory = host.LoggerFactory;
        var log = loggerFactory.CreateLogger("Connect");

        try
        {
            await using var ws = new WsClient(url, loggerFactory.CreateLogger<WsClient>());
            Func<AgentEvent, Task> emit = evt => ws.SendAsync(evt, ct);
            var trace = new Trace(loggerFactory.CreateLogger<Trace>(), emit);
            var agent = host.CreateAgent(trace);

            var capabilities = host.Capabilities.BuildCapabilitiesMessage(host.Kernel);
            await ws.ConnectAsync(capabilities, ct);

            ws.FrameReceived += (type, raw) =>
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        switch (type)
                        {
                            case MessageTypes.Dispatch:
                                await ws.SendAsync(await agent.RunAsync(ContractJson.Deserialize<Dispatch>(raw)), ct);
                                break;
                            case MessageTypes.Approval:
                                await ws.SendAsync(await agent.ApplyApprovalAsync(ContractJson.Deserialize<Approval>(raw)), ct);
                                break;
                            case MessageTypes.Control:
                                var (status, proposal) = await agent.HandleControlAsync(ContractJson.Deserialize<Control>(raw));
                                await ws.SendAsync(status, ct);
                                if (proposal is not null)
                                {
                                    await ws.SendAsync(proposal, ct);
                                }
                                break;
                        }
                    }
                    catch (Exception ex)
                    {
                        log.LogError(ex, "frame handling failed for {Type}", type);
                    }
                }, ct);
                return Task.CompletedTask;
            };

            await ws.RunReceiveLoopAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown (OnTerminate cancelled the token).
        }
        catch (Exception ex)
        {
            // Background-task exceptions are otherwise swallowed — surface to dlog.
            Tizen.Log.Error(DlogLoggerProvider.Tag, $"connect loop FAILED: {ex}");
        }
    }

    protected override void OnTerminate()
    {
        _cts?.Cancel();

        try
        {
            _connectLoop?.Wait(TimeSpan.FromSeconds(5));
            _host?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException ex) when (ex.InnerExceptions.All(static inner => inner is OperationCanceledException))
        {
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _cts?.Dispose();
        }

        base.OnTerminate();
    }

    public static void Main(string[] args)
    {
        // FIRST: prefer app-local assemblies for the ones Tizen also ships (a
        // fallback to the csproj version pins). Must run before any SK type loads.
        AssemblyResolver.Install();
        Tizen.Log.Info(DlogLoggerProvider.Tag, "Main: launching GoalFlow device service");
        new GoalFlowService().Run(args);
    }
}
