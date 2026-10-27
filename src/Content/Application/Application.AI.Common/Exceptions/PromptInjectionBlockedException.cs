namespace Application.AI.Common.Exceptions;

/// <summary>
/// Thrown by <c>PromptInjectionBehavior</c> when a request matches a prompt-injection pattern and its response type
/// cannot carry a <c>GovernanceBlocked</c> result.
/// </summary>
/// <remarks>
/// Derives from <see cref="InvalidOperationException"/>, what the behavior threw before it had its own type, so a caller
/// catching that keeps working; the distinct type is what lets a transport recognise the refusal (#959).
/// </remarks>
public sealed class PromptInjectionBlockedException : InvalidOperationException, IScreeningRefusal
{
    /// <summary>Initializes a new instance of the <see cref="PromptInjectionBlockedException"/> class.</summary>
    public PromptInjectionBlockedException()
        : base("Request blocked: prompt injection detected.")
    {
    }

    /// <summary>Initializes a new instance of the <see cref="PromptInjectionBlockedException"/> class.</summary>
    /// <param name="message">The reason the request was blocked.</param>
    public PromptInjectionBlockedException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="PromptInjectionBlockedException"/> class.</summary>
    /// <param name="message">The reason the request was blocked.</param>
    /// <param name="innerException">The exception that caused this one, or <see langword="null"/>.</param>
    public PromptInjectionBlockedException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    /// <inheritdoc />
    public string ClientMessage => "This message was blocked by the content policy.";
}
