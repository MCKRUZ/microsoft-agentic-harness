namespace Tests.Common;

/// <summary>
/// Redirects a test assembly's on-disk state to a directory of its own for the life of one run,
/// so nothing survives into the next run.
/// </summary>
/// <remarks>
/// <para>
/// Issue #250, then #259. Test assemblies that boot real hosts persist to SQLite paths that resolve
/// against the build output directory, and nothing resets them. State accumulates across every run
/// a developer has ever done on that machine. The Execution API's planner database had reached
/// 540 KB, growing roughly 57 KB per run, before it started returning bad requests that looked
/// exactly like a product regression. CI never sees any of it, because CI always starts from a
/// clean checkout — which is precisely why it survives.
/// </para>
/// <para>
/// <strong>Environment variables, not test configuration.</strong> They are the only source that
/// both outranks <c>appsettings.json</c> and is visible to the eager <c>builder.Configuration</c>
/// reads inside the hosts' service registration. They are also process-global, which is what makes
/// this a whole-assembly concern rather than a per-test one.
/// </para>
/// <para>
/// <strong>Each assembly calls this from its own <c>[ModuleInitializer]</c>.</strong> The
/// initializer cannot live here: a module initializer runs when ITS module loads, and this
/// library's module may not load until after the consuming assembly has already booted a host.
/// The consuming assembly's own initializer is the only hook guaranteed to run first. It also
/// cannot be forgotten the way a shared xunit fixture can, which matters because the failure mode
/// when someone forgets is an intermittent test that reads as a product defect.
/// </para>
/// <para>
/// Governance durable state is deliberately NOT redirected: <c>GovernanceStatePaths.Resolve</c>
/// confines that database to the application directory by design and throws for a path outside it.
/// </para>
/// </remarks>
public static class TestStateRoot
{
    /// <summary>
    /// Points the planner, conversation and knowledge-graph state at a fresh directory named for
    /// this run, sweeps directories abandoned by earlier runs, and registers best-effort cleanup.
    /// </summary>
    /// <param name="prefix">
    /// Distinguishes one assembly's roots from another's — pass something derived from the test
    /// assembly name, e.g. <c>"agenthub-tests-"</c>. Assemblies must not share a prefix, or the
    /// stale-root sweep in one could delete a directory another is still using.
    /// </param>
    /// <returns>The created root directory, for a caller that wants to place state of its own in it.</returns>
    /// <remarks>
    /// Setting a key a given host does not read is harmless, so every caller redirects the same
    /// three rather than each maintaining its own list of what it happens to persist today. A host
    /// that starts persisting something new is then already covered.
    /// </remarks>
    public static string Redirect(string prefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        SweepAbandonedRoots(prefix);

        var root = Path.Combine(Path.GetTempPath(), $"{prefix}{Guid.NewGuid():N}");
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

        return root;
    }

    /// <summary>
    /// Removes roots left by earlier runs that never reached their process-exit cleanup — a run
    /// killed from the IDE, or one whose SQLite handles were still open. Without this, moving the
    /// state out of the build output would relocate the accumulation to the temp directory rather
    /// than ending it. A day's grace keeps the sweep clear of any run still in flight.
    /// </summary>
    private static void SweepAbandonedRoots(string prefix)
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-1);
            foreach (var stale in Directory.EnumerateDirectories(Path.GetTempPath(), $"{prefix}*"))
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
