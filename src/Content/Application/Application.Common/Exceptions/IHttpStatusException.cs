namespace Application.Common.Exceptions;

/// <summary>
/// Implemented by an exception that knows which HTTP status it should surface as.
/// </summary>
/// <remarks>
/// <para>
/// The alternative — a table in the middleware keyed by exception type — cannot express a status for
/// an exception declared in a layer the middleware sits below. <c>SkillPathRefusedException</c> is the
/// case that forced this: it lives in the AI application layer, and the only ways to map it from a
/// central table were to have the lower layer reference the higher one, or to let it inherit a status
/// that is wrong for it. Both are worse than letting the exception answer for itself.
/// </para>
/// <para>
/// Precedence: an exception implementing this interface wins over any inherited mapping, because a
/// type that states its own answer is never guessing. Exceptions that do not implement it are mapped
/// by type as before.
/// </para>
/// <para>
/// <see cref="SafeMessage"/> is what a production client is told. It must reveal nothing the caller
/// was refused — no resource names, no owners, no reason the check failed — because an error body is
/// as readable as a successful one.
/// </para>
/// </remarks>
public interface IHttpStatusException
{
    /// <summary>The HTTP status code this exception should be surfaced as.</summary>
    int StatusCode { get; }

    /// <summary>The message safe to return outside development. Must not disclose refused detail.</summary>
    string SafeMessage { get; }
}
