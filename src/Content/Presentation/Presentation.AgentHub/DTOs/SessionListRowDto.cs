using Domain.AI.Observability.Models;

namespace Presentation.AgentHub.DTOs;

/// <summary>
/// One row of the <c>GET /api/sessions</c> response. Mirrors every property of
/// <see cref="SessionRecord"/> exactly + the Foresight latest context-window
/// <see cref="CategoryBreakdownDto"/> for that conversation. A typed record
/// (vs. an anonymous projection) lets the compiler catch SessionRecord
/// renames at build time and lets Swagger emit a stable response schema.
/// </summary>
public sealed record SessionListRowDto(
    Guid Id,
    string ConversationId,
    string AgentName,
    string? Model,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    int? DurationMs,
    int TurnCount,
    int ToolCallCount,
    int SubagentCount,
    int TotalInputTokens,
    int TotalOutputTokens,
    int TotalCacheRead,
    int TotalCacheWrite,
    decimal TotalCostUsd,
    decimal CacheHitRate,
    string Status,
    string? ErrorMessage,
    DateTimeOffset CreatedAt,
    CategoryBreakdownDto? Breakdown)
{
    /// <summary>Builds a row from a domain record + optional breakdown.</summary>
    public static SessionListRowDto From(SessionRecord session, CategoryBreakdownDto? breakdown)
    {
        ArgumentNullException.ThrowIfNull(session);
        return new SessionListRowDto(
            session.Id,
            session.ConversationId,
            session.AgentName,
            session.Model,
            session.StartedAt,
            session.EndedAt,
            session.DurationMs,
            session.TurnCount,
            session.ToolCallCount,
            session.SubagentCount,
            session.TotalInputTokens,
            session.TotalOutputTokens,
            session.TotalCacheRead,
            session.TotalCacheWrite,
            session.TotalCostUsd,
            session.CacheHitRate,
            session.Status,
            session.ErrorMessage,
            session.CreatedAt,
            breakdown);
    }
}
