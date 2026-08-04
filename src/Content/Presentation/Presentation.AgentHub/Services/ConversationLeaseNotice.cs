namespace Presentation.AgentHub.Services;

/// <summary>
/// Single source of the user-facing copy shown when a turn is stopped because another host took the
/// conversation's turn lease. Shared by the SignalR (<see cref="ConversationOrchestrator"/>) and
/// AG-UI (<c>AgUiRunHandler</c>) transports so the two paths cannot drift, exactly as
/// <see cref="ConversationBudgetNotice"/> is.
/// </summary>
internal static class ConversationLeaseNotice
{
    /// <summary>The message returned to the user in place of the turn that was stopped.</summary>
    public const string Message = "This conversation was continued elsewhere; the turn was stopped.";
}
