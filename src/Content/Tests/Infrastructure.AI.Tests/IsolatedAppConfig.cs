using Domain.Common.Config;

namespace Infrastructure.AI.Tests;

/// <summary>
/// Builds an <see cref="AppConfig"/> whose on-disk paths point at this run's own temporary directory
/// rather than at the build output.
/// </summary>
/// <remarks>
/// <para>
/// Issue #262. Registering the harness's dependencies creates a database wherever the configuration
/// says, and <see cref="AppConfig"/>'s defaults are the production ones — <c>data/planner.db</c> and
/// <c>data/conversations.db</c>, resolved against <c>AppContext.BaseDirectory</c>. A test that hands
/// registration a bare <c>new AppConfig()</c> therefore writes into its own build output, where nothing
/// clears it and it accumulates across every run the machine has ever done.
/// </para>
/// <para>
/// <strong>Use this anywhere a test passes an <see cref="AppConfig"/> into service registration.</strong>
/// <see cref="Create"/> for the common case; <see cref="Isolate"/> to wrap a config a test has built
/// with settings of its own, which keeps the object-initializer shape those tests already use.
/// </para>
/// <para>
/// The paths are absolute, which is what carries them through the
/// <c>Path.Combine(AppContext.BaseDirectory, configured)</c> that registration applies — a relative
/// path would simply land somewhere else inside the build output.
/// </para>
/// </remarks>
internal static class IsolatedAppConfig
{
    /// <summary>A default configuration with its on-disk paths pointed at this run's temp directory.</summary>
    /// <returns>The configuration. Safe to mutate further.</returns>
    public static AppConfig Create() => Isolate(new AppConfig());

    /// <summary>Points an existing configuration's on-disk paths at this run's temp directory.</summary>
    /// <param name="config">The configuration to adjust, typically built with test-specific settings.</param>
    /// <returns>The same instance, for chaining onto an object initializer.</returns>
    /// <remarks>
    /// <para>
    /// Sets the three paths this assembly's registrations are known to write: the planner database, the
    /// conversation database, and the graph data directory. It is <em>not</em> an exhaustive sweep of
    /// every path in <see cref="AppConfig"/> — several others (audit receipt and drift audit paths,
    /// prompt-usage) resolve under the build output too but nothing here currently reaches them.
    /// <c>BuildOutputStaysCleanTests</c> is what catches it if that changes, which is why that guard
    /// scans for databases anywhere under the build output rather than only in the folder these three
    /// use.
    /// </para>
    /// <para>
    /// Governance durable state is deliberately excluded: <c>GovernanceStatePaths.Resolve</c> confines
    /// that database to the application directory by design and throws for a path outside it, so
    /// redirecting it here would break the control rather than isolate it.
    /// </para>
    /// <para>
    /// <strong>Every call gets its own subdirectory, not a shared one.</strong> A single per-assembly
    /// directory kept the databases out of the build output but still handed every test the same file,
    /// and xUnit runs test classes in parallel — so two classes building providers would both find a
    /// table missing, both run <c>EnsureCreated</c>, and one would fail with "table already exists". That
    /// surfaced the moment a hosted service began demanding the conversation schema at construction
    /// (issue #253): a different DI test failed on each run, and none of them was at fault. Sharing a
    /// database between parallel tests is the defect; the hosted service only exposed it.
    /// </para>
    /// </remarks>
    public static AppConfig Isolate(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var root = IsolatedStateRoot.Root;

        // An empty root would silently produce a relative path, which registration then resolves against
        // AppContext.BaseDirectory — putting the database back in the build output this exists to keep
        // clean, and doing it quietly. The module initializer makes this unreachable today; the guard is
        // here so that stops being something a reader has to work out.
        ArgumentException.ThrowIfNullOrWhiteSpace(root, nameof(IsolatedStateRoot.Root));

        // Unique per call: two configurations must never name one database, however many tests are in
        // flight. Created eagerly so a caller that writes a file rather than a database still lands
        // somewhere that exists.
        var slot = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(slot);

        config.AI.Planner.DatabasePath = Path.Combine(slot, "planner.db");
        config.AI.Conversations.DatabasePath = Path.Combine(slot, "conversations.db");
        config.AI.Rag.GraphDatabase.DataDirectory = Path.Combine(slot, "graph");

        return config;
    }
}
