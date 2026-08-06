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
    /// Sets every path this assembly can cause to be written, not only the two currently observed in the
    /// build output. A registration that starts persisting something new is then already covered, and
    /// setting a path a given test never reaches costs nothing.
    /// </remarks>
    public static AppConfig Isolate(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var root = IsolatedStateRoot.Root;

        config.AI.Planner.DatabasePath = Path.Combine(root, "planner.db");
        config.AI.Conversations.DatabasePath = Path.Combine(root, "conversations.db");
        config.AI.Rag.GraphDatabase.DataDirectory = Path.Combine(root, "graph");

        return config;
    }
}
