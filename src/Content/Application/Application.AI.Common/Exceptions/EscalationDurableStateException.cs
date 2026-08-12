using Application.Common.Exceptions;

namespace Application.AI.Common.Exceptions;

/// <summary>
/// Thrown when a durable governance-state write fails and the escalation service must fail
/// closed. Carries a stable, scrubbed <see cref="Code"/> instead of the provider's exception
/// text, so nothing from the data layer (connection strings, file paths, SQL fragments) can
/// reach an HTTP response through the MediatR pipeline's rethrow path.
/// </summary>
/// <remarks>
/// The originating exception is preserved as <see cref="Exception.InnerException"/> for
/// structured logging at the throw site, never for display. Callers that surface failures to
/// a caller should map <see cref="Code"/>, not <see cref="Exception.Message"/>.
/// </remarks>
public sealed class EscalationDurableStateException : ApplicationExceptionBase
{
    /// <summary>
    /// A decision could not be durably recorded in the working-state store. The decision was
    /// not accepted; the approver may retry once the store recovers.
    /// </summary>
    public const string DurableWriteFailedCode = "escalation.durable_write_failed";

    /// <summary>
    /// A resolution could not be durably recorded. The escalation is not reported resolved and
    /// stays observable for the reconciler.
    /// </summary>
    public const string DurableResolutionFailedCode = "escalation.durable_resolution_failed";

    /// <summary>
    /// An escalation could not be durably created, so it was not opened.
    /// </summary>
    public const string DurableCreateFailedCode = "escalation.durable_create_failed";

    /// <summary>Initializes a new instance with a stable code and the originating exception.</summary>
    /// <param name="code">One of the <c>escalation.*</c> constants on this type.</param>
    /// <param name="innerException">The provider exception, preserved for logging only.</param>
    public EscalationDurableStateException(string code, Exception? innerException = null)
        : base(code, innerException)
    {
        Code = code;
    }

    /// <summary>The stable, scrubbed error code safe to surface to a caller.</summary>
    public string Code { get; }
}
