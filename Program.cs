using GoalFlow.Device.Transport;
using Tizen.Applications;

namespace GoalFlow.Device;

/// <summary>
/// Headless Tizen service host. The portable GoalFlow pipeline lives outside
/// this class; the service only owns lifecycle, configuration, and transport.
/// </summary>
public sealed class GoalFlowService : ServiceApplication
{
    private CancellationTokenSource? _cts;
    private WsClient? _client;
    private Task? _connectLoop;

    protected override void OnCreate()
    {
        base.OnCreate();

        PipelineFactory.LoadDotEnv(Path.Combine(Directory.GetCurrentDirectory(), ".env"));

        var host = PipelineFactory.Build(new PipelineFactoryOptions
        {
            DataDir = Environment.GetEnvironmentVariable("GOALFLOW_DATA_DIR") ?? PipelineFactory.DefaultDataDir,
        });

        var wsUrl = Environment.GetEnvironmentVariable("WS_URL") ?? "ws://localhost:8000/ws";
        _cts = new CancellationTokenSource();
        _client = new WsClient(new Uri(wsUrl), host.Pipeline, host.Trace);

        // ServiceApplication must return from OnCreate; the WebSocket transport
        // owns the long-running reconnect loop on a background task.
        _connectLoop = Task.Run(() => _client.RunAsync(_cts.Token), _cts.Token);
    }

    protected override void OnTerminate()
    {
        _cts?.Cancel();

        try
        {
            _connectLoop?.Wait(TimeSpan.FromSeconds(5));
            _client?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
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
