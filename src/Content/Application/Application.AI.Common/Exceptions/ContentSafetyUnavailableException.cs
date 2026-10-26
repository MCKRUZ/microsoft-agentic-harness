using Application.Common.Exceptions;

namespace Application.AI.Common.Exceptions;

/// <summary>
/// Thrown when content could not be screened (the safety service is unreachable, throttled, rejected the credential, or
/// returned an incomplete analysis) and the deployment does not let unscreened content through (#904).
/// </summary>
/// <remarks>
/// <para>
/// An outage is not a verdict on the content, so it is not reported as a block: a <see cref="ContentSafetyException"/>
/// or a <c>ContentBlocked</c> result tells the caller the text itself was harmful, which is permanent and not worth
/// retrying, and counts toward the block metrics a deployment alerts on. This exception surfaces as a service failure
/// instead, which the caller can retry once the service is back.
/// </para>
/// <para>
/// The message never contains the screened text.
/// </para>
/// </remarks>
public sealed class ContentSafetyUnavailableException : ApplicationExceptionBase
{
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
