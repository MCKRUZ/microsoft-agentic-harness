using Domain.AI.Context;
using Presentation.AgentHub.DTOs;

namespace Presentation.AgentHub.Extensions;

/// <summary>
/// Domain → DTO mapping for the Foresight context-snapshot pipeline. Plain
/// projections — no AutoMapper because the shapes are tiny and the wire
/// contract is more important than the abstraction.
/// </summary>
public static class ContextSnapshotMappingExtensions
{
    /// <summary>Maps a domain breakdown to its DTO.</summary>
    public static CategoryBreakdownDto ToDto(this CategoryBreakdown breakdown)
    {
        ArgumentNullException.ThrowIfNull(breakdown);
        return new CategoryBreakdownDto(
            breakdown.System,
            breakdown.Agents,
            breakdown.Skills,
            breakdown.Tools,
            breakdown.Mcp,
            breakdown.Messages);
    }

    /// <summary>Maps a domain loaded item to its DTO. Category emitted as the
    /// canonical lowercase wire string via <see cref="ContextCategoryWireExtensions.ToWire"/>.</summary>
    public static LoadedItemDto ToDto(this LoadedItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return new LoadedItemDto(
            item.What,
            item.Tokens,
            item.Category.ToWire(),
            item.Reference);
    }

    /// <summary>Maps a domain snapshot to its DTO.</summary>
    public static ContextSnapshotDto ToDto(this ContextSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new ContextSnapshotDto(
            snapshot.ConversationId,
            snapshot.TurnIndex,
            snapshot.TurnId,
            snapshot.CtxAfter.ToDto(),
            [.. snapshot.Loaded.Select(ToDto)],
            snapshot.CapturedAtUtc);
    }
}
