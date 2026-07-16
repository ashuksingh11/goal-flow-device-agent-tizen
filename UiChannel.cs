using System.Collections.Concurrent;
using GoalFlow.Device.Contracts;
using Tizen.Applications;
using Tizen.Applications.Messages;

namespace GoalFlow.Device;

/// <summary>
/// TIZEN-ONLY seam that mirrors the device agent's progress to the on-Hub NUI UI
/// (<c>org.goalflow.tizenui</c>, repo <c>goal-flow-agent-tizen-ui</c>). It:
///   1. LAUNCHES the UI via App Control when a goal activates the agent, and
///   2. FORWARDS the frames the agent already emits (agent_event / plan_ready /
///      proposal / status) to the UI over a PUBLIC Tizen Message Port.
///
/// It is one-way (display-only UI): the only inbound message is a <c>ui_ready</c>
/// handshake from the UI. DECOUPLING: this file + the small tee in
/// <see cref="GoalFlowService"/> are the ONLY device-side additions — the portable
/// core (<c>Agent/</c>, <c>Contracts/</c>, <c>Modules/</c>, <c>Transport/WsClient.cs</c>)
/// is untouched, so ubuntu→tizen re-syncs stay a plain copy. Serialization reuses
/// <see cref="ContractJson.Serialize"/> (a call, not an edit).
///
/// PORTS (public, no privilege): the agent registers a local <c>goalflow.agent.control</c>
/// port to receive <c>ui_ready</c>, and sends frames to the UI's <c>goalflow.ui.frames</c>
/// port. Everything is best-effort and non-throwing — the cloud path is the durable
/// one; a UI hiccup must never affect planning.
/// </summary>
public sealed class UiChannel : IDisposable
{
    private const string UiAppId = "org.goalflow.tizenui";
    private const string UiFramesPort = "goalflow.ui.frames";
    private const string ControlPort = "goalflow.agent.control";
    private const string BundleKey = "frame";
    private const int BufferCap = 256;

    private readonly ConcurrentQueue<string> _buffer = new();
    private readonly object _gate = new();

    private MessagePort? _controlPort;   // local: receives ui_ready + sends frames to the UI
    private RemotePort? _uiPort;         // remote handle to the UI's frames port (IsRunning + state)
    private bool _uiReady;

    /// <summary>Register the control port + watch the UI's port. Call once at startup
    /// (well before any dispatch), so the ui_ready handshake is never missed.</summary>
    public void Start()
    {
        try
        {
            _controlPort = new MessagePort(ControlPort, trusted: false);
            _controlPort.MessageReceived += OnControlMessage;
            _controlPort.Listen();
        }
        catch (Exception ex)
        {
            Log($"control port listen failed: {ex.Message}");
        }

        try
        {
            _uiPort = new RemotePort(UiAppId, UiFramesPort, trusted: false);
            _uiPort.RemotePortStateChanged += OnRemoteStateChanged;
        }
        catch (Exception ex)
        {
            Log($"remote port watch failed: {ex.Message}");
        }
    }

    /// <summary>A goal just activated the agent (first dispatch): a new goal owns the
    /// screen, so drop stale beats + re-handshake, push a synthetic <c>ui_goal</c>
    /// (carries the objective for Beat 1 — no agent frame does), and launch the UI.</summary>
    public void OnGoalActivated(string goalId, string objective)
    {
        while (_buffer.TryDequeue(out _))
        {
        }
        lock (_gate)
        {
            _uiReady = false;
        }

        Forward(new { Type = "ui_goal", GoalId = goalId, Objective = objective });
        LaunchUi();
    }

    /// <summary>Tee one outbound frame to the UI. Serializes with the same
    /// <see cref="ContractJson"/> the cloud transport uses, then sends or buffers.
    /// Never throws, never blocks the caller.</summary>
    public void Forward<T>(T frame)
    {
        string json;
        try
        {
            json = ContractJson.Serialize(frame);
        }
        catch (Exception ex)
        {
            Log($"serialize failed: {ex.Message}");
            return;
        }
        SendOrBuffer(json);
    }

    private void SendOrBuffer(string json)
    {
        bool ready;
        lock (_gate)
        {
            ready = _uiReady;
        }

        // Self-heal: if ui_ready was lost but the UI's port is live, adopt it.
        if (!ready && TryRemoteRunning())
        {
            lock (_gate)
            {
                _uiReady = true;
            }
            Flush();
            ready = true;
        }

        if (ready && TrySend(json))
        {
            return;
        }

        _buffer.Enqueue(json);
        while (_buffer.Count > BufferCap && _buffer.TryDequeue(out _))
        {
        }
    }

    private void Flush()
    {
        while (_buffer.TryDequeue(out var json))
        {
            if (!TrySend(json))
            {
                // UI went away mid-flush: re-buffer and wait for the next handshake.
                _buffer.Enqueue(json);
                lock (_gate)
                {
                    _uiReady = false;
                }
                return;
            }
        }
    }

    private bool TrySend(string json)
    {
        try
        {
            using var bundle = new Bundle();
            bundle.AddItem(BundleKey, json);
            _controlPort?.Send(bundle, UiAppId, UiFramesPort);
            return true;
        }
        catch (Exception ex)
        {
            Log($"send failed (UI not up yet?): {ex.Message}");
            return false;
        }
    }

    private bool TryRemoteRunning()
    {
        try
        {
            return _uiPort is not null && _uiPort.IsRunning();
        }
        catch
        {
            return false;
        }
    }

    private void OnControlMessage(object? sender, MessageReceivedEventArgs e)
    {
        // The only message the UI sends is ui_ready — treat any control message as
        // "the UI is listening" and flush whatever we buffered during launch.
        try
        {
            var bundle = e.Message;
            var payload = bundle is not null && bundle.Contains(BundleKey) && bundle.GetItem(BundleKey) is string s
                ? s
                : "";
            Log($"ui handshake: {payload}");
        }
        catch (Exception ex)
        {
            Log($"control message read failed: {ex.Message}");
        }

        lock (_gate)
        {
            _uiReady = true;
        }
        Flush();
    }

    private void OnRemoteStateChanged(object? sender, RemotePortStateChangedEventArgs e)
    {
        if (e.Status == State.Registered)
        {
            lock (_gate)
            {
                _uiReady = true;
            }
            Flush();
        }
        else
        {
            lock (_gate)
            {
                _uiReady = false; // UI closed — buffer again until it comes back
            }
        }
    }

    private void LaunchUi()
    {
        _ = Task.Run(() =>
        {
            try
            {
                var appControl = new AppControl
                {
                    ApplicationId = UiAppId,
                    Operation = AppControlOperations.Default,
                };
                AppControl.SendLaunchRequest(appControl);
                Log($"launched {UiAppId}");
            }
            catch (Exception ex)
            {
                Log($"launch failed: {ex.Message}");
            }
        });
    }

    public void Dispose()
    {
        try
        {
            if (_controlPort is not null)
            {
                _controlPort.MessageReceived -= OnControlMessage;
                _controlPort.StopListening();
                _controlPort.Dispose();
                _controlPort = null;
            }
        }
        catch
        {
            // best-effort teardown
        }

        try
        {
            if (_uiPort is not null)
            {
                _uiPort.RemotePortStateChanged -= OnRemoteStateChanged;
                _uiPort.Dispose();
                _uiPort = null;
            }
        }
        catch
        {
            // best-effort teardown
        }
    }

    private static void Log(string message)
        => Tizen.Log.Info(DlogLoggerProvider.Tag, $"[UiChannel] {message}");
}
