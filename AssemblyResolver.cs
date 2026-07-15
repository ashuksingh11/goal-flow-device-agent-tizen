using System.Reflection;
using System.Runtime.Loader;

namespace GoalFlow.Device;

/// <summary>
/// TIZEN EDGE (fallback): prefer app-local (bin) copies of assemblies that the
/// Tizen platform also ships. The PRIMARY fix for the System.Text.Json conflict
/// is the version pins in the csproj (align to the .NET 8 line — Tizen 12 ships
/// STJ 8.0.x whose assembly version 8.0.0.0 matches any 8.0.x). This resolver is
/// belt-and-suspenders: if an app-local copy of one of these assemblies exists in
/// the app base dir, load THAT rather than falling through to a platform version.
///
/// LIMITATION (be honest): <see cref="AssemblyLoadContext.Resolving"/> is a
/// FALLBACK raised only after normal probing fails — it CANNOT override a
/// framework assembly the Tizen launcher already loaded from the platform TPA.
/// So it helps only when the reference wasn't already satisfied (e.g. the
/// platform doesn't provide the assembly), not to force a downgrade/upgrade of an
/// already-loaded one. Version alignment remains the real fix.
/// </summary>
public static class AssemblyResolver
{
    private static readonly string[] PreferAppLocal =
    {
        "System.Text.Json",
        "System.Text.Encodings.Web",
    };

    private static int _installed;

    /// <summary>Register the resolver once. Call FIRST in Main, before any SK type.</summary>
    public static void Install()
    {
        if (Interlocked.Exchange(ref _installed, 1) == 1)
        {
            return;
        }

        AssemblyLoadContext.Default.Resolving += ResolveAppLocal;
    }

    private static Assembly? ResolveAppLocal(AssemblyLoadContext context, AssemblyName name)
    {
        if (name.Name is null || Array.IndexOf(PreferAppLocal, name.Name) < 0)
        {
            return null;
        }

        var candidate = Path.Combine(AppContext.BaseDirectory, name.Name + ".dll");
        if (!File.Exists(candidate))
        {
            return null;
        }

        try
        {
            var loaded = context.LoadFromAssemblyPath(candidate);
            Tizen.Log.Info(DlogLoggerProvider.Tag, $"resolved {name.Name} app-local from {candidate}");
            return loaded;
        }
        catch (Exception ex)
        {
            Tizen.Log.Warn(DlogLoggerProvider.Tag, $"app-local resolve failed for {name.Name}: {ex.Message}");
            return null;
        }
    }
}
