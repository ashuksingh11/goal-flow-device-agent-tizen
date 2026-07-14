namespace GoalFlow.Device;

/// <summary>
/// TIZEN EDGE: configuration source that does NOT rely on process environment
/// variables. A Tizen <c>ServiceApplication</c> is not launched with the shell
/// environment (so <c>OPENROUTER_API_KEY</c> etc. come back null) and its working
/// directory is not the app directory (so a CWD-relative <c>.env</c> is never
/// found). Instead, config is read from a <c>goalflow.conf</c> (KEY=VALUE) file
/// bundled in the app's resource dir, with a writable Data-dir override, and an
/// environment-variable fallback so the SAME code still works in a desktop /
/// Ubuntu-parity run.
///
/// Lookup precedence per key: environment variable → <c>Data/goalflow.conf</c>
/// (writable, on-device drop-in) → <c>Resource/goalflow.conf</c> (bundled) →
/// CWD <c>goalflow.conf</c>/<c>.env</c> (desktop) → null.
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
            $"{key} is required. Set it in goalflow.conf (in the app resource or data dir) " +
            "or, off-device, in the environment.");

    /// <summary>
    /// Resolve a WRITABLE mock-world directory. <see cref="Modules.Capabilities.MockWorldStore"/>
    /// mutates <c>shopping_list.json</c> etc., but the .tpk bundles <c>data/</c>
    /// read-only under <c>Resource</c> — writing there fails. Seed a writable copy
    /// under the app <c>Data</c> dir on first run and use that. Off-Tizen (no app
    /// framework) this is just <c>./data</c>.
    /// </summary>
    public string ResolveDataDir()
    {
        if (Get("GOALFLOW_DATA_DIR") is { Length: > 0 } configured)
        {
            return configured;
        }

        var writable = AppPath(static d => d.Data, "data");
        if (writable is null)
        {
            return "data"; // not running under the Tizen app framework
        }

        // Seed the writable copy from the first bundled source we can find.
        var source = AppPath(static d => d.Resource, "data")
            ?? Path.Combine(Directory.GetCurrentDirectory(), "data");
        SeedIfEmpty(source, writable);
        return writable;
    }

    private static void SeedIfEmpty(string source, string target)
    {
        var hasTarget = Directory.Exists(target) && Directory.EnumerateFiles(target, "*.json").Any();
        if (hasTarget || !Directory.Exists(source))
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
        if (AppPath(static d => d.Resource, "goalflow.conf") is { } resource)
        {
            yield return resource;
        }

        if (AppPath(static d => d.Data, "goalflow.conf") is { } data)
        {
            yield return data;
        }

        var cwd = Directory.GetCurrentDirectory();
        yield return Path.Combine(cwd, "goalflow.conf");
        yield return Path.Combine(cwd, ".env");
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
