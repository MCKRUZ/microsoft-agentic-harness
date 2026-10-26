using Application.Common.Exceptions;

namespace Application.AI.Common.Exceptions;

/// <summary>
/// Thrown by an <see cref="Interfaces.Agent.ITextContentSafetyService"/> when content could not be screened: the safety
/// service is unreachable, throttled or timed out, the credential cannot be obtained or is rejected, or the analysis
/// came back incomplete (#904). <c>ContentSafetyBehavior</c> catches it and applies the deployment's
/// <c>BlockWhenUnavailable</c> policy: refuse the request with this exception, or let it through unscreened.
/// </summary>
/// <remarks>
/// <para>
/// An outage is not a verdict on the content, so it is not reported as a block: a <see cref="ContentSafetyException"/>
/// or a <c>ContentBlocked</c> result tells the caller the text itself was harmful, which is permanent and not worth
/// retrying, and counts toward the block metrics a deployment alerts on. This exception surfaces as a service failure
/// instead: HTTP 503, which the caller can retry once the service is back.
/// </para>
/// <para>
/// The message never contains the screened text.
/// </para>
/// </remarks>
public sealed class ContentSafetyUnavailableException : ApplicationExceptionBase, IHttpStatusException
{
    /// <inheritdoc />
    public int StatusCode => 503;

    /// <inheritdoc />
    public string SafeMessage => "Content could not be screened.";

    /// <summary>Initializes a new instance of the <see cref="ContentSafetyUnavailableException"/> class.</summary>
    public ContentSafetyUnavailableException()
        : base("Content could not be screened: the safety service is unavailable.")
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ContentSafetyUnavailableException"/> class.</summary>
    /// <param name="message">The message that describes the failure.</param>
    public ContentSafetyUnavailableException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ContentSafetyUnavailableException"/> class.</summary>
    /// <param name="message">The message that describes the failure.</param>
    /// <param name="innerException">The failure that stopped screening, or <see langword="null"/>.</param>
    public ContentSafetyUnavailableException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
