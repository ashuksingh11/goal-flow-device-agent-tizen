using GoalFlow.Device.Agent;
using GoalFlow.Device.Harness;
using GoalFlow.Device.Products.FamilyHub;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;

namespace GoalFlow.Device;

/// <summary>
/// Tizen host wiring for the portable v3 GoalFlow core. This is the ONLY
/// platform-specific seam besides <see cref="GoalFlowService"/> (the
/// ServiceApplication host), <see cref="DeviceConfig"/> (env-free config) and
/// <see cref="DlogLoggerProvider"/> (dlog logging): it builds the same
/// dependency-injection container as the Ubuntu <c>Program.cs</c> so the SK
/// agent + the five harness components + the FamilyHub product pack run
/// byte-for-byte unchanged on the Family Hub.
///
/// v3 STRUCTURE (re-synced from Ubuntu at M9): the flat v2 <c>Modules/</c> split
/// into <c>Harness/</c> (the five generic components — Capability Manager, Safety
/// Policy Engine, Pre-check Engine, Task Manager, Product API Adapter) and
/// <c>Products/FamilyHub/</c> (the product pack, registered by one line —
/// <see cref="FamilyHubProduct.AddFamilyHub"/>). LLM-ONLY still: planning goes
/// through the SK kernel + OpenRouter, no scripted planner.
///
/// PLATFORM NOTES (why this can't be a byte-copy of Ubuntu's Program.cs): config
/// is read via <see cref="DeviceConfig"/> (a bundled <c>goalflow.conf</c>), NOT
/// environment variables — a Tizen service is not launched with the shell
/// environment. Logging goes to dlog, NOT the console — a headless service has no
/// stdout. And the safety policy + prechecks ship as csproj &lt;Content&gt; under
/// <c>Products/FamilyHub/config/</c>, resolved from <c>AppContext.BaseDirectory</c>
/// (== the bundle's bin on Tizen); if that Content item is dropped the loaders
/// return EMPTY silently and the Hub plans with no safety enforcement.
/// </summary>
public sealed class DeviceHost : IAsyncDisposable
{
    public ServiceProvider Provider { get; }
    public ILoggerFactory LoggerFactory { get; }
    public IClock Clock { get; }
    public Kernel Kernel { get; }
    public CapabilityManager Capabilities { get; }
    public AgentSettings Settings { get; }

    private DeviceHost(
        ServiceProvider provider,
        ILoggerFactory loggerFactory,
        IClock clock,
        Kernel kernel,
        CapabilityManager capabilities,
        AgentSettings settings)
    {
        Provider = provider;
        LoggerFactory = loggerFactory;
        Clock = clock;
        Kernel = kernel;
        Capabilities = capabilities;
        Settings = settings;
    }

    /// <summary>Build the DI container + kernel. Mirrors Ubuntu Program.cs.</summary>
    public static DeviceHost Build(DeviceConfig config, string dataDir)
    {
        var services = new ServiceCollection();

        // dlog (NOT console) — a headless Tizen service has no stdout.
        services.AddLogging(logging => logging
            .ClearProviders()
            .AddProvider(new DlogLoggerProvider())
            .SetMinimumLevel(ParseLogLevel(config) ?? LogLevel.Information));

        // GENERIC CLOCK: SimulatedClock anchored at real today (or GOALFLOW_DATE),
        // so the demo's set_date / advance_day controls work. No hardcoded anchor.
        services.AddSingleton<IClock>(_ =>
            config.Get("GOALFLOW_DATE") is { Length: > 0 } start
                ? new SimulatedClock(DateOnly.Parse(start))
                : new SimulatedClock());

        // THE PRODUCT PACK: the mock world (behind IProductApiAdapter), the capability
        // plugins, the CapabilityManager, the domain observers, the prechecks, and the
        // proactive suggester — all in one line. This is the ONLY line here that knows
        // what product this is.
        services.AddFamilyHub(dataDir);

        // Harness components (generic — no product types).
        services.AddSingleton<SafetyFilter>();
        services.AddSingleton<ApprovalCoordinator>();
        services.AddSingleton<Grounding>();
        services.AddSingleton<MonitorAdapt>();
        services.AddSingleton<PrecheckEngine>();

        var provider = services.BuildServiceProvider();
        var loggerFactory = provider.GetRequiredService<ILoggerFactory>();

        var settings = new AgentSettings
        {
            ApiKey = config.GetRequired("OPENROUTER_API_KEY"),
            BaseUrl = config.Get("OPENROUTER_BASE_URL", "https://openrouter.ai/api/v1"),
            ModelId = config.Get("OPENROUTER_MODEL", "openai/gpt-oss-120b"),
        };
        // Per-call LLM deadlines are tunable via goalflow.conf (raise if a slow/large model
        // cancels planning mid-compose) — override only when set to a positive int.
        if (int.TryParse(config.Get("LLM_CALL_TIMEOUT_SECONDS"), out var llmCallTimeout) && llmCallTimeout > 0)
            settings = settings with { LlmCallTimeoutSeconds = llmCallTimeout };
        if (int.TryParse(config.Get("LLM_STREAM_TIMEOUT_SECONDS"), out var llmStreamTimeout) && llmStreamTimeout > 0)
            settings = settings with { LlmStreamTimeoutSeconds = llmStreamTimeout };
        // HARNESS_DWELL_MS (v5, presenter mode): >0 holds each harness engine's spotlight so a
        // demo audience can watch the pipeline light up. 0/unset = OFF (real timing). Allow 0.
        if (int.TryParse(config.Get("HARNESS_DWELL_MS"), out var harnessDwell) && harnessDwell >= 0)
            settings = settings with { HarnessDwellMs = harnessDwell };
        var kernel = GoalAgent.BuildKernel(settings, provider);

        return new DeviceHost(
            provider,
            loggerFactory,
            provider.GetRequiredService<IClock>(),
            kernel,
            provider.GetRequiredService<CapabilityManager>(),
            settings);
    }

    /// <summary>
    /// Build a <see cref="GoalAgent"/> bound to a <see cref="Trace"/> whose
    /// agent_event stream is emitted by the caller (the transport). The
    /// <see cref="TaskManager"/> is constructed here too, wired to the trace so every
    /// task transition streams a task_update (Agent Board's progress comes from it) —
    /// both depend on the live WebSocket's emit, so neither can live in the container.
    /// </summary>
    public GoalAgent CreateAgent(Trace trace)
    {
        var tasks = new TaskManager(
            LoggerFactory.CreateLogger<TaskManager>(),
            (goal, task) => trace.TaskUpdateAsync(
                task, goal.ProgressPercent, goal.PendingTasks, NextStep(goal)));

        return new GoalAgent(
            Kernel,
            trace,
            Provider.GetRequiredService<Grounding>(),
            Provider.GetRequiredService<SafetyFilter>(),
            Provider.GetRequiredService<ApprovalCoordinator>(),
            Provider.GetRequiredService<MonitorAdapt>(),
            Provider.GetRequiredService<CapabilityManager>(),
            tasks,
            Provider.GetRequiredService<PrecheckEngine>(),
            Clock,
            LoggerFactory.CreateLogger<GoalAgent>(),
            Settings);
    }

    /// <summary>The goal's frontier task title — Agent Board's "Next Step".</summary>
    private static string? NextStep(GoalRecord goal)
        => goal.Tasks.FirstOrDefault(t => !t.IsTerminal && t.State != TaskState.Monitoring)?.Title;

    private static LogLevel? ParseLogLevel(DeviceConfig config)
        => Enum.TryParse<LogLevel>(config.Get("LOG_LEVEL"), ignoreCase: true, out var level)
            ? level
            : null;

    public async ValueTask DisposeAsync() => await Provider.DisposeAsync();
}
