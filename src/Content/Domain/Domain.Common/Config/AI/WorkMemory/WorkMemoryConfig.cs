namespace Domain.Common.Config.AI.WorkMemory;

/// <summary>
/// Root configuration for the self-improving work-memory subsystem. Bound from
/// <c>AppConfig:AI:WorkMemory</c> in appsettings.json.
/// </summary>
/// <remarks>
/// <para>
/// Work memory records what the agent <em>did</em> on each turn (a <c>WorkEpisode</c>), so a later
/// overnight synthesis pass can distill those trajectories into reusable lessons. This config governs
/// the capture half (PR1).
/// </para>
/// <para>
/// <strong>Off by default.</strong> Capturing episodes is pointless until the synthesis (PR2) and
/// recall (PR3) consumers exist, so a fresh consumer pays no cost and stores nothing until they
/// deliberately opt in.
/// </para>
/// <code>
/// AppConfig.AI.WorkMemory
/// ├── Enabled                 — Master toggle for episode capture (default false)
/// ├── StoreProvider           — Keyed DI provider ("graph" or "in_memory")
/// └── ResponseSummaryMaxChars — Cap on stored response length (bounds episode size)
/// </code>
/// </remarks>
public class WorkMemoryConfig
{
    /// <summary>
    /// Master toggle. When disabled (the default), <c>WorkEpisodeCaptureBehavior</c> is a pass-through
    /// and no episodes are recorded.
    /// </summary>
    /// <value>Default: false</value>
    public bool Enabled { get; set; }

    /// <summary>
    /// Keyed DI provider for <c>IWorkEpisodeStore</c> ("graph" or "in_memory").
    /// </summary>
    /// <value>Default: "graph"</value>
    public string StoreProvider { get; set; } = "graph";

    /// <summary>
    /// Maximum number of characters of the assistant response stored on an episode. Responses longer
    /// than this are truncated at capture time to bound per-episode storage. Must be positive.
    /// </summary>
    /// <value>Default: 2000</value>
    public int ResponseSummaryMaxChars { get; set; } = 2000;
}
