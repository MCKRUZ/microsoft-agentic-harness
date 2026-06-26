namespace Domain.Common.Config.AI;

/// <summary>
/// Configuration for recalling relevant learnings into the agent's context at turn start (the read
/// half of the self-improving loop). Bound from <c>AppConfig:AI:LearningsRecall</c>.
/// </summary>
/// <remarks>
/// <para>
/// When enabled, <c>LearningsRecallContextProvider</c> recalls the learnings most relevant to the
/// current task — across every source, including the global self-improvement lessons written by the
/// work-memory synthesis pass — and injects them into the agent's instructions before the model runs.
/// </para>
/// <para>
/// <strong>Off by default.</strong> Recall runs the relevance scoring pipeline (which embeds the query
/// and candidate learnings) on every turn, so a consumer opts in deliberately. Keep
/// <see cref="MaxResults"/> small and <see cref="MinRelevance"/> meaningful so only genuinely similar
/// past work is surfaced rather than diluting the prompt with weakly-related lessons.
/// </para>
/// </remarks>
public sealed class LearningsRecallConfig
{
    /// <summary>
    /// Master toggle. When disabled (the default), no recall provider is wired and nothing is injected.
    /// </summary>
    /// <value>Default: false</value>
    public bool Enabled { get; set; }

    /// <summary>
    /// Maximum number of learnings injected per turn. Kept small to bound prompt budget. Must be positive.
    /// </summary>
    /// <value>Default: 3</value>
    public int MaxResults { get; set; } = 3;

    /// <summary>
    /// Minimum semantic relevance (0.0-1.0) a learning must have to the current task to be injected.
    /// A meaningful floor keeps recall to "this task resembles past work" rather than loosely-related
    /// lessons. Must be in the range [0, 1].
    /// </summary>
    /// <value>Default: 0.3</value>
    public double MinRelevance { get; set; } = 0.3;
}
