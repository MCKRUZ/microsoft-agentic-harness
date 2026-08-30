namespace Presentation.AgentHub.Services;

/// <summary>
/// Single source of the user-facing copy shown when a retry is refused because the conversation has
/// no preceding user message to retry. Mirrors <see cref="ConversationLeaseNotice"/>'s shape: a
/// dedicated notice type rather than a hand-typed literal at both the throw site
/// (<see cref="ConversationOrchestrator"/>) and the hub's exact-match check
/// (<c>AgentTelemetryHub.NotifyPostTruncationFailureAndMapAsync</c>), so the two cannot drift and a
/// substring check against arbitrary <see cref="InvalidOperationException"/> text is never needed.
/// </summary>
internal static class ConversationRetryNotice
{
    /// <summary>The message returned when a retry has no preceding user message to resubmit.</summary>
    public const string NoPrecedingUserMessage = "Cannot retry: no preceding user message found.";
}
