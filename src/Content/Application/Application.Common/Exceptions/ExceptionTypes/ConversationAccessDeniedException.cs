namespace Application.Common.Exceptions.ExceptionTypes;

/// <summary>
/// Thrown when a caller reaches for a conversation that exists but belongs to another user.
/// </summary>
/// <remarks>
/// <para>
/// Derives from <see cref="UnauthorizedAccessException"/> so that transports already catching that
/// type — the SignalR hub does, in six places — keep behaving exactly as before without edits. The
/// distinct type is what lets everything else tell an ownership refusal apart from the unrelated
/// <see cref="UnauthorizedAccessException"/> the file-backed conversation store raises when the
/// operating system holds a file lock through three retries. Mapping the base type to
/// <c>403 Forbidden</c> would dress that transient I/O failure as an authorization decision.
/// </para>
/// <para>
/// It sits here beside <see cref="ForbiddenAccessException"/> rather than with the other AI-facing
/// exceptions because <c>GlobalExceptionMiddleware</c> maps exceptions by <em>exact</em> type, and
/// that middleware can only see this assembly. Declared anywhere else it would inherit no mapping
/// and surface as a 500 — a refusal the code made correctly, reported as a server fault.
/// </para>
/// </remarks>
public sealed class ConversationAccessDeniedException : UnauthorizedAccessException
{
    /// <summary>Creates the refusal.</summary>
    /// <remarks>
    /// No message overload, deliberately. A refusal has exactly one thing it may say, because
    /// anything more specific — naming the owner, confirming the conversation exists, explaining
    /// which check failed — answers the question the caller was refused. Leaving the message
    /// unconfigurable makes that a property of the type rather than a rule each throw site has to
    /// keep.
    /// </remarks>
    public ConversationAccessDeniedException()
        : base("Access denied.")
    {
    }
}
