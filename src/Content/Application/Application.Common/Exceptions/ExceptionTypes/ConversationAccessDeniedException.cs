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
    /// <summary>Creates the exception with the default caller-facing message.</summary>
    public ConversationAccessDeniedException()
        : base("Access denied.")
    {
    }

    /// <summary>Creates the exception with a specific caller-facing message.</summary>
    /// <param name="message">
    /// The message shown to the caller. Must not name the owner or otherwise confirm what the
    /// conversation is — that would answer the question the caller was refused.
    /// </param>
    public ConversationAccessDeniedException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception wrapping an underlying cause.</summary>
    /// <param name="message">The message shown to the caller.</param>
    /// <param name="innerException">The underlying cause.</param>
    public ConversationAccessDeniedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
