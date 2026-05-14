namespace Domain.AI.Skills;

/// <summary>
/// A learned instruction amendment associated with a skill. Amendments are stored
/// in the knowledge graph and appended to skill instructions at Tier 2 loading time.
/// They participate in the compliance layer automatically via <see cref="OwnerId"/>.
/// </summary>
public record SkillAmendment
{
    /// <summary>Unique identifier for this amendment.</summary>
    public required string Id { get; init; }
    /// <summary>The skill this amendment applies to.</summary>
    public required string SkillId { get; init; }
    /// <summary>The learned instruction text to append to skill instructions.</summary>
    public required string Content { get; init; }
    /// <summary>What triggered this amendment (e.g., query type, user feedback).</summary>
    public required string LearnedFrom { get; init; }
    /// <summary>When this amendment was created.</summary>
    public required DateTimeOffset CreatedAt { get; init; }
    /// <summary>Scoped to a user/tenant, or null for global amendments.</summary>
    public string? OwnerId { get; init; }
}
