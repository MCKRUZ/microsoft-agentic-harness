namespace Domain.Common.Config.AI.WorkMemory;

/// <summary>
/// Root configuration for the self-improving work-memory subsystem. Bound from
/// <c>AppConfig:AI:WorkMemory</c> in appsettings.json.
/// </summary>
/// <remarks>
/// <para>
/// Work memory records what the agent <em>did</em> on each turn (a <c>WorkEpisode</c>), so a later
/// overnight synthesis pass can distill those trajectories into reusable lessons. This config governs
/// both the capture half (PR1) and the overnight synthesis half (PR2).
/// </para>
/// <para>
/// <strong>Off by default.</strong> Capturing episodes is pointless until the synthesis (PR2) and
/// recall (PR3) consumers exist, so a fresh consumer pays no cost and stores nothing until they
/// deliberately opt in. Synthesis has its own nested toggle (<see cref="SynthesisEnabled"/>) so a
/// consumer can capture episodes without yet paying the LLM cost of distilling them.
/// </para>
/// <code>
/// AppConfig.AI.WorkMemory
/// ├── Enabled                 — Master toggle for episode capture (default false)
/// ├── StoreProvider           — Keyed DI provider ("graph" or "in_memory")
/// ├── ResponseSummaryMaxChars — Cap on stored response length (bounds episode size)
/// ├── SynthesisEnabled        — Nested toggle for the overnight synthesis pass (default false)
/// ├── SynthesisIntervalHours  — How often the synthesis pass runs
/// ├── SynthesisLookbackHours  — Episode window each run distills
/// ├── MaxEpisodesPerRun       — Upper bound on episodes read per run (bounds LLM cost)
/// └── MinConfidenceToStore    — Drop synthesized lessons below this confidence
/// </code>
/// </remarks>
public class WorkMemoryConfig
{
    /// <summary>
    /// Master toggle. When disabled (the default), <c>WorkEpisodeCaptureBehavior</c> is a pass-through
    /// and no episodes are recorded. Also gates the synthesis pass: synthesis never registers while the
    /// subsystem is off, regardless of <see cref="SynthesisEnabled"/>.
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

    /// <summary>
    /// Nested toggle for the overnight synthesis pass (PR2). When disabled (the default), no
    /// <c>WorkMemorySynthesisBackgroundService</c> is registered and captured episodes are never
    /// distilled into lessons. Requires <see cref="Enabled"/> to also be true to take effect.
    /// </summary>
    /// <value>Default: false</value>
    public bool SynthesisEnabled { get; set; }

    /// <summary>
    /// Interval, in hours, between synthesis passes. "Overnight" cadence is the intended default, but
    /// the value is free so a consumer can run more frequently. Must be positive.
    /// </summary>
    /// <value>Default: 24</value>
    public double SynthesisIntervalHours { get; set; } = 24;

    /// <summary>
    /// Look-back window, in hours, of episodes each synthesis pass reads (<c>CreatedAfter = now -
    /// lookback</c>). Decoupled from <see cref="SynthesisIntervalHours"/> so a pass can overlap the
    /// previous window (cheap insurance against missed episodes at a boundary). Must be positive.
    /// </summary>
    /// <value>Default: 24</value>
    public double SynthesisLookbackHours { get; set; } = 24;

    /// <summary>
    /// Upper bound on the number of episodes a single synthesis pass reads, bounding the LLM context
    /// and cost per run. The most recent episodes within the look-back window are taken. Must be positive.
    /// </summary>
    /// <value>Default: 200</value>
    public int MaxEpisodesPerRun { get; set; } = 200;

    /// <summary>
    /// Minimum confidence a synthesized lesson must carry to be persisted. Lessons the synthesizer
    /// reports below this threshold are dropped before the security gate and the store write. Must be
    /// in the range [0, 1].
    /// </summary>
    /// <value>Default: 0.7</value>
    public double MinConfidenceToStore { get; set; } = 0.7;
}
