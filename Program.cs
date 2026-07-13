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

        DeviceHost.LoadDotEnv(Path.Combine(Directory.GetCurrentDirectory(), ".env"));
        var dataDir = Environment.GetEnvironmentVariable("GOALFLOW_DATA_DIR") ?? "data";
        _host = DeviceHost.Build(dataDir);

        var wsUrl = Environment.GetEnvironmentVariable("WS_URL") ?? "ws://localhost:8000/ws";
        _cts = new CancellationTokenSource();
        _connectLoop = Task.Run(() => RunAsync(new Uri(wsUrl), _cts.Token));
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
        new GoalFlowService().Run(args);
    }
}
