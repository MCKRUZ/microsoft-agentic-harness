namespace Domain.AI.Caching;

/// <summary>
/// Shared conventions between the caller that builds a static system prompt
/// (<c>Application.AI.Common.Factories.AgentExecutionContextFactory</c>) and the transform that
/// stamps Anthropic prompt-cache breakpoints onto the outgoing request
/// (<c>Infrastructure.AI.Caching.PromptCacheInjector</c>). Lives in Domain because Application must
/// not depend on Infrastructure and vice versa, and both sides need to agree on this exact string.
/// </summary>
public static class PromptCacheConventions
{
    /// <summary>
    /// Sentinel appended to the end of stable system content to mark, explicitly, where the
    /// cache-eligible prefix ends — see <c>PromptCacheInjector</c>'s "Marker-based override" remarks
    /// for the full rationale. Never sent to the model: the appender
    /// (<c>AgentExecutionContextFactory</c>) only emits it when
    /// <c>AppConfig:AI:AgentFramework:EnablePromptCaching</c> is true, which is also the only
    /// condition under which <c>PromptCacheInjector</c> runs at all — so on every path where the
    /// marker could appear, something is guaranteed to strip it before the request leaves the
    /// process. With caching off, no marker is ever appended and per-turn context still reaches the
    /// model normally through the same <c>AIContextProvider</c> rail; it just isn't cache-isolated,
    /// which is moot when nothing is being cached.
    /// </summary>
    public const string CacheBoundaryMarker = "<<cache-boundary-8f2e6c1a>>";
}
