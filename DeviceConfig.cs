namespace GoalFlow.Device;

/// <summary>
/// TIZEN EDGE: configuration source that does NOT rely on process environment
/// variables. A Tizen <c>ServiceApplication</c> is not launched with the shell
/// environment (so <c>OPENROUTER_API_KEY</c> etc. come back null) and its working
/// directory is not the app directory (so a CWD-relative <c>.env</c> is never
/// found). Config is read from a <c>goalflow.conf</c> (KEY=VALUE) file
/// bundled with the app, with a writable Data-dir override, and an
/// environment-variable fallback so the SAME code still works in a desktop /
/// Ubuntu-parity run.
///
/// FOLDER MAPPING (why <see cref="AppContext.BaseDirectory"/>, not
/// <c>Resource</c>): the csproj bundles <c>goalflow.conf</c> and <c>data/**</c>
/// as MSBuild <c>Content</c>, which Tizen packaging drops next to the app
/// assemblies under <c>bin</c> — that path is <see cref="AppContext.BaseDirectory"/>
/// at runtime and IS readable by the app (the managed DLLs load from it). It is
/// NOT <c>DirectoryInfo.Resource</c> (<c>res/</c>), which stays empty unless a
/// build explicitly packages into <c>res/</c>. So bundled files are read from
/// <c>bin</c> first, with <c>res</c> kept as a fallback.
///
/// Lookup precedence per key: environment variable → bundled
/// <c>bin/goalflow.conf</c> (<see cref="AppContext.BaseDirectory"/>) → bundled
/// <c>Resource/goalflow.conf</c> → CWD <c>goalflow.conf</c>/<c>.env</c> (desktop)
/// → writable <c>Data/goalflow.conf</c> (on-device drop-in, wins over bundled)
/// → null.
/// </summary>
public sealed class DeviceConfig
{
    private readonly IReadOnlyDictionary<string, string> _values;

    private DeviceConfig(IReadOnlyDictionary<string, string> values) => _values = values;

    public static DeviceConfig Load()
    {
        // Later files win, so load bundled first, then the writable/CWD overrides.
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in ConfigCandidatePaths())
        {
            LoadFile(path, values);
        }

        return new DeviceConfig(values);
    }

    /// <summary>env var (desktop) → conf file (Tizen) → null.</summary>
    public string? Get(string key)
        => Environment.GetEnvironmentVariable(key) is { Length: > 0 } env
            ? env
            : _values.TryGetValue(key, out var value) && value.Length > 0 ? value : null;

    public string Get(string key, string fallback) => Get(key) ?? fallback;

    public string GetRequired(string key)
        => Get(key) ?? throw new InvalidOperationException(
            $"{key} is required. Set it in goalflow.conf (bundled next to the app under " +
            "bin, or dropped in the app Data dir) or, off-device, in the environment.");

    /// <summary>
    /// Resolve a WRITABLE mock-world directory. <see cref="Modules.Capabilities.MockWorldStore"/>
    /// mutates <c>shopping_list.json</c> etc., but the bundled <c>data/</c> ships
    /// read-only next to the app assemblies (under <c>bin</c> ==
    /// <see cref="AppContext.BaseDirectory"/>) — writing there fails. Seed a
    /// writable copy into the app's private <c>Data</c> ROOT on first run and use
    /// that (NOT a <c>Data/data</c> sub-dir). Off-Tizen (no app framework) this is
    /// just <c>./data</c>.
    /// </summary>
    public string ResolveDataDir()
    {
        if (Get("GOALFLOW_DATA_DIR") is { Length: > 0 } configured)
        {
            return configured;
        }

        // The app's private writable data ROOT (e.g. <app>/data), NOT <app>/data/data.
        var writable = AppDataRoot();
        if (writable is null)
        {
            return "data"; // not running under the Tizen app framework (desktop/Ubuntu parity)
        }

        SeedIfEmpty(BundledDataDir(), writable);
        return writable;
    }

    /// <summary>
    /// The READ-ONLY bundled <c>data/</c> dir. Tizen packaging drops our
    /// <c>Content</c> (<c>data/**</c>) next to the app assemblies under <c>bin</c>,
    /// which is <see cref="AppContext.BaseDirectory"/> at runtime and readable by
    /// the app; prefer it. Fall back to the Tizen resource dir (if a build packages
    /// <c>data/</c> there) and finally the CWD (desktop).
    /// </summary>
    private static string BundledDataDir()
    {
        var baseDir = Path.Combine(AppContext.BaseDirectory, "data");
        if (HasJson(baseDir))
        {
            return baseDir;
        }

        if (AppPath(static d => d.Resource, "data") is { } resource && HasJson(resource))
        {
            return resource;
        }

        return Path.Combine(Directory.GetCurrentDirectory(), "data");
    }

    /// <summary>The app's private WRITABLE data root (<c>&lt;app&gt;/data</c>), or null off-Tizen.</summary>
    private static string? AppDataRoot()
    {
        try
        {
            return Tizen.Applications.Application.Current?.DirectoryInfo?.Data;
        }
        catch
        {
            return null;
        }
    }

    private static bool HasJson(string dir)
        => Directory.Exists(dir) && Directory.EnumerateFiles(dir, "*.json").Any();

    private static void SeedIfEmpty(string source, string target)
    {
        if (HasJson(target) || !Directory.Exists(source))
        {
            return;
        }

        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source, "*.json"))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        }
    }

    private static IEnumerable<string> ConfigCandidatePaths()
    {
        // Loaded in order; later files OVERRIDE earlier ones (LoadFile overwrites),
        // so bundled sources come first and the writable on-device drop-in comes last.

        // Bundled: Content lands next to the app assemblies under <app>/bin ==
        // AppContext.BaseDirectory (readable). This is where our goalflow.conf goes.
        yield return Path.Combine(AppContext.BaseDirectory, "goalflow.conf");

        // Fallback: the Tizen resource dir, in case a build packages it to <app>/res.
        if (AppPath(static d => d.Resource, "goalflow.conf") is { } resource)
        {
            yield return resource;
        }

        // Desktop / Ubuntu-parity run.
        var cwd = Directory.GetCurrentDirectory();
        yield return Path.Combine(cwd, "goalflow.conf");
        yield return Path.Combine(cwd, ".env");

        // On-device WRITABLE drop-in (last → overrides the bundled copy).
        if (AppPath(static d => d.Data, "goalflow.conf") is { } data)
        {
            yield return data;
        }
    }

    /// <summary>
    /// Resolve a path under a Tizen app directory (Resource / Data), or null when
    /// not running under the Tizen app framework. Fully qualifies the Tizen type
    /// to avoid clashing with <see cref="System.IO.DirectoryInfo"/>.
    /// </summary>
    private static string? AppPath(Func<Tizen.Applications.DirectoryInfo, string> pick, string relative)
    {
        try
        {
            var dir = Tizen.Applications.Application.Current?.DirectoryInfo;
            return dir is null ? null : Path.Combine(pick(dir), relative);
        }
        catch
        {
            return null;
        }
    }

    private static void LoadFile(string path, IDictionary<string, string> into)
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

            into[line[..equals].Trim()] = line[(equals + 1)..].Trim().Trim('"');
        }
    }
}
