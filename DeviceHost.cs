using GoalFlow.Device.Agent;
using GoalFlow.Device.Modules.Capabilities;
using GoalFlow.Device.Modules.Steering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;

namespace GoalFlow.Device;

/// <summary>
/// Tizen host wiring for the portable v2 GoalFlow core. This is the ONLY
/// platform-specific seam besides <see cref="GoalFlowService"/> (the
/// ServiceApplication host), <see cref="DeviceConfig"/> (env-free config) and
/// <see cref="DlogLoggerProvider"/> (dlog logging): it builds the same
/// dependency-injection container as the Ubuntu <c>Program.cs</c> so the SK
/// agent + capability plugins + steering modules run byte-for-byte unchanged on
/// the Family Hub.
///
/// v2 is LLM-ONLY (planning goes through the SK kernel + OpenRouter — there is
/// no rules/scripted planner) and the world is a concrete <see cref="MockWorldStore"/>
/// over bundled <c>data/*.json</c>. The v1 <c>GOALFLOW_ADAPTERS=mock|tizen</c>
/// adapter-interface seam is gone; wiring real Tizen actuators (calendar,
/// notifications, appliances) is future work behind the capability plugins.
///
/// PLATFORM NOTE: config is read via <see cref="DeviceConfig"/> (a bundled
/// <c>goalflow.conf</c>), NOT environment variables — a Tizen service is not
/// launched with the shell environment. Logging goes to dlog, NOT the console —
/// a headless service has no stdout, so <c>AddConsole()</c> crashes on the Hub.
/// </summary>
public sealed class DeviceHost : IAsyncDisposable
{
    public ServiceProvider Provider { get; }
    public ILoggerFactory LoggerFactory { get; }
    public IClock Clock { get; }
    public Kernel Kernel { get; }
    public CapabilityRegistry Capabilities { get; }

    private DeviceHost(
        ServiceProvider provider,
        ILoggerFactory loggerFactory,
        IClock clock,
        Kernel kernel,
        CapabilityRegistry capabilities)
    {
        Provider = provider;
        LoggerFactory = loggerFactory;
        Clock = clock;
        Kernel = kernel;
        Capabilities = capabilities;
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

        // Mock world + capability plugins (meal + guest domains + shared).
        services.AddSingleton(sp => new MockWorldStore(dataDir, sp.GetRequiredService<IClock>()));
        services.AddSingleton<InventoryPlugin>();
        services.AddSingleton<CalendarPlugin>();
        services.AddSingleton<RecipePlugin>();
        services.AddSingleton<ShoppingListPlugin>();
        services.AddSingleton<ReminderPlugin>();
        services.AddSingleton<GuestsPlugin>();
        services.AddSingleton<ApplianceControlPlugin>();
        services.AddSingleton<FamilyProfilesPlugin>();
        services.AddSingleton<BudgetPlugin>();
        services.AddSingleton<NotifyPlugin>();

        // Steering modules.
        services.AddSingleton<SafetyFilter>();
        services.AddSingleton<ApprovalCoordinator>();
        services.AddSingleton<Grounding>();
        services.AddSingleton<MaterialityPolicy>();
        services.AddSingleton<MonitorAdapt>();
        services.AddSingleton<CapabilityRegistry>();

        var provider = services.BuildServiceProvider();
        var loggerFactory = provider.GetRequiredService<ILoggerFactory>();

        var settings = new AgentSettings
        {
            ApiKey = config.GetRequired("OPENROUTER_API_KEY"),
            BaseUrl = config.Get("OPENROUTER_BASE_URL", "https://openrouter.ai/api/v1"),
            ModelId = config.Get("OPENROUTER_MODEL", "openai/gpt-oss-120b"),
        };
        var kernel = GoalAgent.BuildKernel(settings, provider);

        return new DeviceHost(
            provider,
            loggerFactory,
            provider.GetRequiredService<IClock>(),
            kernel,
            provider.GetRequiredService<CapabilityRegistry>());
    }

    /// <summary>
    /// Build a <see cref="GoalAgent"/> bound to a <see cref="Trace"/> whose
    /// agent_event stream is emitted by the caller (the transport). Kept off the
    /// container because <c>emit</c> depends on the live WebSocket.
    /// </summary>
    public GoalAgent CreateAgent(Trace trace) => new(
        Kernel,
        trace,
        Provider.GetRequiredService<Grounding>(),
        Provider.GetRequiredService<SafetyFilter>(),
        Provider.GetRequiredService<ApprovalCoordinator>(),
        Provider.GetRequiredService<MonitorAdapt>(),
        Clock,
        LoggerFactory.CreateLogger<GoalAgent>());

    private static LogLevel? ParseLogLevel(DeviceConfig config)
        => Enum.TryParse<LogLevel>(config.Get("LOG_LEVEL"), ignoreCase: true, out var level)
            ? level
            : null;

    public async ValueTask DisposeAsync() => await Provider.DisposeAsync();
}
