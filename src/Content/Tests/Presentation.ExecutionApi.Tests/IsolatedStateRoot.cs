using System.Runtime.CompilerServices;

namespace Presentation.ExecutionApi.Tests;

/// <summary>
/// Gives this assembly's test run its own on-disk state, so nothing survives from the last run.
/// </summary>
/// <remarks>
/// <para>
/// Issue #250. The hosts these tests boot persist to SQLite paths that resolve against the build
/// output directory, and nothing reset them between runs. State therefore accumulated across every
/// run a developer had ever done on that machine — the planner database here had reached 540 KB and
/// grew by roughly 57 KB per run. CI never saw it, because CI always starts from a clean checkout,
/// which is exactly why it survived. The failure it eventually produces presents as bad requests
/// from the API, so anyone reading the test output alone goes looking in the product.
/// </para>
/// <para>
/// The redirection happens through ENVIRONMENT VARIABLES rather than per-test configuration for the
/// reason already recorded in <c>AssemblyInfo.cs</c>: they are the only source that both outranks
/// appsettings.json and is visible to the eager <c>builder.Configuration</c> read inside
/// <c>AddExecutionApiServices</c>. Doing it in a module initializer covers all sixteen host
/// constructions in this assembly at once, and — unlike a per-class fixture — cannot be missed by
/// the next test class someone adds.
/// </para>
/// <para>
/// Governance durable state is deliberately NOT redirected: <c>GovernanceStatePaths.Resolve</c>
/// confines that database to the application directory by design and throws for a path outside it.
/// </para>
/// </remarks>
internal static class IsolatedStateRoot
{
    private const string RootPrefix = "execapi-tests-";

    [ModuleInitializer]
    internal static void Redirect()
    {
        SweepAbandonedRoots();

        var root = Path.Combine(Path.GetTempPath(), $"{RootPrefix}{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        // Absolute paths survive the hosts' Path.Combine(AppContext.BaseDirectory, configured)
        // unchanged, which is what moves the file out of the build output.
        Environment.SetEnvironmentVariable(
            "AppConfig__AI__Planner__DatabasePath", Path.Combine(root, "planner.db"));
        Environment.SetEnvironmentVariable(
            "AppConfig__AI__Conversations__DatabasePath", Path.Combine(root, "conversations.db"));
        Environment.SetEnvironmentVariable(
            "AppConfig__AI__Rag__GraphDatabase__DataDirectory", Path.Combine(root, "graph"));

        AppDomain.CurrentDomain.ProcessExit += (_, _) => TryDelete(root);
    }

    /// <summary>
    /// Removes roots left by earlier runs that never reached their process-exit cleanup — a run
    /// killed from the IDE, or one whose SQLite handles were still open. Without this, moving the
    /// state out of the build output would just relocate the accumulation to the temp directory.
    /// A day's grace keeps it clear of any run still in flight on the same machine.
    /// </summary>
    private static void SweepAbandonedRoots()
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-1);
            foreach (var stale in Directory.EnumerateDirectories(Path.GetTempPath(), $"{RootPrefix}*"))
            {
                if (Directory.GetCreationTimeUtc(stale) < cutoff)
                    TryDelete(stale);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Best-effort cleanup. A SQLite handle the host has not finished releasing can still hold the
    /// file at process exit; leaving a temp directory behind is a far smaller problem than failing
    /// the run over it, and the per-run name means the next run is unaffected either way.
    /// </summary>
    private static void TryDelete(string root)
    {
        try
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
