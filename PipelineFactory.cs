using GoalFlow.Device.Harnesses;
using GoalFlow.Device.Harnesses.Adapters;

namespace GoalFlow.Device;

/// <summary>
/// Tizen host wiring for the copied GoalFlow core. This mirrors the Ubuntu
/// BuildPipeline helper while keeping platform selection at the adapter edge.
/// </summary>
public static class PipelineFactory
{
    public const string DefaultPlanner = "rules";
    public const string DefaultDataDir = "data";
    public const string DefaultAdapterSet = "mock";

    private static readonly DateTimeOffset ClockAnchor = DateTimeOffset.Parse("2026-07-12T09:00:00+00:00");

    public static PipelineHost Build(PipelineFactoryOptions? options = null)
    {
        options ??= new PipelineFactoryOptions();

        var dataDir = options.DataDir ?? DefaultDataDir;
        var plannerName = options.Planner ?? Environment.GetEnvironmentVariable("GOALFLOW_PLANNER") ?? DefaultPlanner;
        var adapterSet = options.AdapterSet ?? Environment.GetEnvironmentVariable("GOALFLOW_ADAPTERS") ?? DefaultAdapterSet;
        var clock = new VirtualClock(ClockAnchor);
        var trace = new InMemoryTrace();
        var seedStore = new DataSeedStore(dataDir);

        var rulesPlanner = new RulesPlanner(trace, clock);
        var scriptedPlanner = new ScriptedPlanner(Path.Combine(dataDir, "golden-plan_ready.json"));
        IPlanner planner = plannerName switch
        {
            "rules" => rulesPlanner,
            "scripted" => scriptedPlanner,
            "llm" => new LlmPlanner(
                new LlmPlannerOptions
                {
                    ApiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY"),
                    BaseUrl = Environment.GetEnvironmentVariable("OPENROUTER_BASE_URL") ?? "https://openrouter.ai/api/v1",
                    Model = Environment.GetEnvironmentVariable("OPENROUTER_MODEL") ?? "anthropic/claude-sonnet-5",
                },
                rulesPlanner,
                trace,
                clock),
            _ => throw new ArgumentException($"Unknown planner '{plannerName}'. Use rules, llm, or scripted."),
        };

        var adapters = CreateAdapters(adapterSet, dataDir);
        var grounding = new Grounding(
            adapters.Inventory,
            adapters.Calendar,
            adapters.Recipes,
            adapters.ShoppingList,
            adapters.Reminders,
            clock,
            trace);

        var pipeline = new Pipeline(
            planner,
            grounding,
            new SafetyGate(trace, clock),
            new ApprovalBroker(clock, trace),
            new EffectExecutor(adapters.ShoppingList, adapters.Reminders, clock, trace),
            new Scheduler(clock, trace),
            new ChangeWatcher(clock, trace),
            clock,
            trace,
            ClockAnchor,
            seedStore.Restore);

        return new PipelineHost(pipeline, trace, clock, dataDir, plannerName, adapterSet);
    }

    public static void LoadDotEnv(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        foreach (var rawLine in File.ReadAllLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var equals = line.IndexOf('=');
            if (equals <= 0)
            {
                continue;
            }

            var key = line[..equals].Trim();
            var value = line[(equals + 1)..].Trim().Trim('"');
            if (Environment.GetEnvironmentVariable(key) is null)
            {
                Environment.SetEnvironmentVariable(key, value);
            }
        }
    }

    public static void CopyDirectory(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        foreach (var file in Directory.EnumerateFiles(sourceDir))
        {
            File.Copy(file, Path.Combine(targetDir, Path.GetFileName(file)), overwrite: true);
        }
    }

    private static AdapterSet CreateAdapters(string adapterSet, string dataDir) =>
        adapterSet switch
        {
            "mock" => new AdapterSet(
                new MockInventoryApi(Path.Combine(dataDir, "inventory.json")),
                new MockCalendarApi(Path.Combine(dataDir, "calendar.json")),
                new MockRecipeApi(Path.Combine(dataDir, "recipes.json")),
                new MockShoppingListApi(Path.Combine(dataDir, "shopping_list.json")),
                new MockReminderApi(Path.Combine(dataDir, "reminders.json"))),
            "tizen" => new AdapterSet(
                new TizenInventoryApi(),
                new TizenCalendarApi(),
                new TizenRecipeApi(),
                new TizenShoppingListApi(),
                new TizenReminderApi()),
            _ => throw new ArgumentException($"Unknown adapter set '{adapterSet}'. Use mock or tizen."),
        };

    private sealed record AdapterSet(
        IInventoryApi Inventory,
        ICalendarApi Calendar,
        IRecipeApi Recipes,
        IShoppingListApi ShoppingList,
        IReminderApi Reminders);
}

public sealed record PipelineFactoryOptions
{
    public string? Planner { get; init; }

    public string? DataDir { get; init; }

    public string? AdapterSet { get; init; }
}

public sealed record PipelineHost(
    Pipeline Pipeline,
    ITrace Trace,
    IClock Clock,
    string DataDir,
    string Planner,
    string AdapterSet);

internal sealed class DataSeedStore
{
    private readonly string _dataDir;
    private readonly Dictionary<string, byte[]> _seed = new(StringComparer.Ordinal);

    public DataSeedStore(string dataDir)
    {
        _dataDir = dataDir;
        foreach (var file in Directory.EnumerateFiles(dataDir, "*.json"))
        {
            _seed[Path.GetFileName(file)] = File.ReadAllBytes(file);
        }
    }

    public void Restore()
    {
        Directory.CreateDirectory(_dataDir);
        foreach (var file in Directory.EnumerateFiles(_dataDir, "*.json"))
        {
            var name = Path.GetFileName(file);
            if (!_seed.ContainsKey(name))
            {
                File.Delete(file);
            }
        }

        foreach (var (name, bytes) in _seed)
        {
            File.WriteAllBytes(Path.Combine(_dataDir, name), bytes);
        }
    }
}
