namespace Application.AI.Common.Exceptions;

/// <summary>
/// A stand-in for an exception whose message must not be logged: same type name, same stack trace, same chain of inner
/// exceptions, but no message text (#907).
/// </summary>
/// <remarks>
/// <para>
/// Erasure failures are logged with the exception so an operator can tell what broke, but a store's exception message
/// can carry the very identity being erased: a file store's <see cref="IOException"/> names a path built from a
/// conversation id, and a search service's request failure can echo the owner filter it was sent. The log pipeline's
/// redaction matches patterns (emails, keys, tokens) and lets such ids through, so the message is withheld here, at the
/// call site, instead. Log <c>MessageWithheldException.From(ex)</c> in place of <c>ex</c>.
/// </para>
/// <para>
/// Only the first inner exception of each level is kept, which is all <see cref="Exception.InnerException"/> carries.
/// </para>
/// </remarks>
public sealed class MessageWithheldException : Exception
{
    private readonly string? _stackTrace;

    private MessageWithheldException(Exception original, Exception? inner)
        : base($"{original.GetType().FullName} (message withheld: it may name the person being erased)", inner)
    {
        _stackTrace = original.StackTrace;
        HResult = original.HResult;
    }

    /// <summary>Where the original exception was thrown.</summary>
    public override string? StackTrace => _stackTrace;

    /// <summary>Copies <paramref name="exception"/> and every inner exception without their messages.</summary>
    /// <param name="exception">The exception to log.</param>
    /// <returns>The stand-in to log.</returns>
    public static MessageWithheldException From(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return new MessageWithheldException(
            exception, exception.InnerException is null ? null : From(exception.InnerException));
    }
}
