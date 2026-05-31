namespace Infrastructure.AI.Prompts.Persistence;

/// <summary>
/// EF-mapped row for a single prompt usage event. Mirrors
/// <see cref="Application.AI.Common.Prompts.Models.PromptUsageRecord"/> plus a
/// synthetic primary key so concurrent writers don't collide on a natural key.
/// </summary>
/// <remarks>
/// Schema: indexed on TraceId, CaseId, and RecordedAtUtc so trace-replay queries
/// can range-scan efficiently. PromptName + PromptVersion + PromptHash are kept
/// alongside the prompt descriptor's identifying fields so historical rows remain
/// readable even if a prompt file is later deleted from the registry.
/// </remarks>
public sealed class PromptUsageEntity
{
    /// <summary>Autoincrement surrogate key.</summary>
    public long Id { get; set; }

    /// <summary>Registry name (e.g. <c>faithfulness-judge</c>).</summary>
    public string PromptName { get; set; } = string.Empty;

    /// <summary>Major version part.</summary>
    public int PromptVersionMajor { get; set; }

    /// <summary>Minor version part.</summary>
    public int PromptVersionMinor { get; set; }

    /// <summary>SHA-256 content hash of the resolved prompt body (hex, lowercase).</summary>
    public string PromptHash { get; set; } = string.Empty;

    /// <summary>OTel/W3C trace id (hex, no dashes). Nullable when no ambient activity existed.</summary>
    public string? TraceId { get; set; }

    /// <summary>OTel/W3C span id (hex, no dashes). Nullable when no ambient activity existed.</summary>
    public string? SpanId { get; set; }

    /// <summary>Case / unit-of-work identifier supplied by the caller.</summary>
    public string? CaseId { get; set; }

    /// <summary>Consuming surface identifier (metric key, command name).</summary>
    public string? MetricKey { get; set; }

    /// <summary>UTC timestamp the usage was recorded.</summary>
    public DateTimeOffset RecordedAtUtc { get; set; }
}
