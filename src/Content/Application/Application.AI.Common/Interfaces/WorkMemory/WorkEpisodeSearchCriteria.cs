using Domain.AI.WorkMemory;

namespace Application.AI.Common.Interfaces.WorkMemory;

/// <summary>
/// Filter criteria for querying <see cref="WorkEpisode"/> records. All properties are optional;
/// a null property means "no filter on that dimension". The primary consumer is the overnight
/// synthesis pass, which scans recent episodes (typically <see cref="CreatedAfter"/> = last 24h).
/// </summary>
public sealed record WorkEpisodeSearchCriteria
{
    /// <summary>Restrict to a single conversation. Null = all conversations in scope.</summary>
    public string? ConversationId { get; init; }

    /// <summary>Restrict to episodes with this outcome. Null = both successes and failures.</summary>
    public EpisodeOutcome? Outcome { get; init; }

    /// <summary>Only episodes created at or after this instant. Null = no lower bound.</summary>
    public DateTimeOffset? CreatedAfter { get; init; }

    /// <summary>Only episodes created at or before this instant. Null = no upper bound.</summary>
    public DateTimeOffset? CreatedBefore { get; init; }

    /// <summary>Maximum number of episodes to return. Null = no limit (caller is responsible for bounding).</summary>
    public int? Limit { get; init; }
}
