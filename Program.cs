using GoalFlow.Device.Contracts;
using GoalFlow.Device.Harness;
using GoalFlow.Device.Transport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tizen.Applications;

namespace GoalFlow.Device;

/// <summary>
/// Headless Tizen service host for the v3 GoalFlow device agent. This class owns
/// ONLY platform lifecycle + transport; the portable SK agent (DI container, the
/// five harness components, the FamilyHub product pack) is built by
/// <see cref="DeviceHost"/> and runs unchanged from the Ubuntu build.
///
/// <see cref="ServiceApplication.OnCreate"/> must return promptly, so the
/// long-running connect/receive loop runs on a background task; the WebSocket
/// transport (<see cref="WsClient"/>) owns connect-retry + reconnect-on-drop.
///
/// v3-M9: the device-side on-Hub UI was dropped, so the App-Control launch + Message
/// Port forwarding (the old <c>UiChannel</c>) is gone — this host now does only the
/// cloud path, exactly like Ubuntu.
/// </summary>
public sealed class GoalFlowService : ServiceApplication
{
    private CancellationTokenSource? _cts;
    private DeviceHost? _host;
    private Task? _connectLoop;
    private string _deviceId = "";
    private string _deviceName = "";

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

            // The cloud is MULTI-SESSION: it pairs this Hub with its UIs by device_id.
            // Self-generated + persisted in the writable data dir, so this Hub keeps one
            // identity across restarts with no configuration.
            _deviceId = config.ResolveDeviceId(dataDir);
            _deviceName = config.ResolveDeviceName(_deviceId);
            Tizen.Log.Info(DlogLoggerProvider.Tag, $"OnCreate: device_id={_deviceId} device_name={_deviceName}");

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
            await using var ws = new WsClient(url, loggerFactory.CreateLogger<WsClient>(), _deviceId, _deviceName);
            var trace = new Trace(loggerFactory.CreateLogger<Trace>(), evt => ws.SendAsync(evt, ct));
            var agent = host.CreateAgent(trace);

            var capabilities = host.Capabilities.BuildCapabilitiesMessage(host.Kernel);
            await ws.ConnectAsync(capabilities, ct);

            // Proactive suggestions (v3-M8): scan local state and offer goals the
            // family hasn't asked for. Emitted on connect and after every control tick
            // (advance_day / reset move the world, so the list is recomputed).
            var suggesters = host.Provider.GetServices<ISuggester>().ToArray();
            async Task EmitSuggestionsAsync()
            {
                try
                {
                    var items = new List<SuggestionItem>();
                    foreach (var suggester in suggesters)
                    {
                        items.AddRange(await suggester.ScanAsync(ct));
                    }
                    await ws.SendAsync(new SuggestionsMessage { Items = items }, ct);
                    log.LogInformation("suggestions emitted count={Count}", items.Count);
                }
                catch (Exception ex)
                {
                    log.LogError(ex, "suggestion scan failed");
                }
            }
            await EmitSuggestionsAsync();

            ws.FrameReceived += (type, raw) =>
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        switch (type)
                        {
                            case MessageTypes.Dispatch:
                                var planReady = await agent.RunAsync(ContractJson.Deserialize<Dispatch>(raw));
                                await ws.SendAsync(planReady, ct);
                                break;
                            case MessageTypes.Approval:
                                var approvalStatus = await agent.ApplyApprovalAsync(ContractJson.Deserialize<Approval>(raw));
                                await ws.SendAsync(approvalStatus, ct);
                                break;
                            case MessageTypes.Control:
                                var control = ContractJson.Deserialize<Control>(raw);
                                if (string.IsNullOrEmpty(control.GoalId) && control.Command != ControlCommands.TriggerEvent)
                                {
                                    // WORLD-level tick (v3.2): advance the clock once, fan out to
                                    // every active goal, and summarise the day's world events.
                                    var world = await agent.HandleWorldControlAsync(control);
                                    foreach (var s in world.Statuses) await ws.SendAsync(s, ct);
                                    foreach (var p in world.Proposals) await ws.SendAsync(p, ct);
                                    if (world.DayAdvanced is not null) await ws.SendAsync(world.DayAdvanced, ct);
                                }
                                else
                                {
                                    // Per-goal control (a trigger_event) — the older scoped path.
                                    var (status, proposal) = await agent.HandleControlAsync(control);
                                    await ws.SendAsync(status, ct);
                                    if (proposal is not null)
                                    {
                                        await ws.SendAsync(proposal, ct);
                                    }
                                }
                                // The world moved — re-scan so a suggestion that came
                                // true or went stale is reflected.
                                await EmitSuggestionsAsync();
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
