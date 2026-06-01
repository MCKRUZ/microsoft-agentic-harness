namespace Presentation.AgentHub.DTOs;

/// <summary>
/// Wire shape for <see cref="Domain.AI.Context.CategoryBreakdown"/>. Property names
/// are part of the public JSON contract — both the SignalR <c>ContextSnapshot</c>
/// payload and the <c>/api/sessions</c> + <c>/api/sessions/:id</c> HTTP responses
/// serialise these. Mirrors the frontend <c>CategoryBreakdown</c> type in
/// <c>src/Content/Presentation/Presentation.Dashboard/src/api/types.ts</c>.
/// </summary>
public sealed record CategoryBreakdownDto(
    int System,
    int Agents,
    int Skills,
    int Tools,
    int Mcp,
    int Messages);
