using Application.AI.Common.Exceptions;
using Application.Common.Exceptions.ExceptionTypes;
using Xunit;

namespace Presentation.AgentHub.Tests;

/// <summary>
/// Every way a turn is rejected before the model sees the caller's message (#959), with what the caller is told.
/// </summary>
/// <remarks>
/// The messages are literals on purpose: they are what the caller sees, so they are pinned here rather than read back
/// from the production code that produces them.
/// </remarks>
internal static class RejectionCases
{
    private const string Blocked = "This message was blocked by the content policy.";
    private const string NotAccepted = "This message could not be accepted. Check its length and try again.";

    /// <summary>Every rejection, with the message the caller is told.</summary>
    public static TheoryData<Exception, string> All => new()
    {
        { new ContentSafetyException("Content blocked by safety policy (hate).", "hate"), Blocked },
        { new ContentSafetyUnavailableException(), "Content could not be screened right now. Try again shortly." },
        { new PromptInjectionBlockedException("Prompt injection detected (role_play). Request blocked."), Blocked },
        // Validation runs before screening, so a message it rejects was never screened at all.
        { new ValidationException("TurnContext: Message and turn context together exceed the maximum length."), NotAccepted },
    };

    /// <summary>The rejections that are a verdict on the message, as opposed to a screening outage.</summary>
    public static TheoryData<Exception, string> Verdicts => new()
    {
        { new ContentSafetyException("Content blocked by safety policy (hate).", "hate"), Blocked },
        { new PromptInjectionBlockedException("Prompt injection detected (role_play). Request blocked."), Blocked },
        { new ValidationException("UserMessage: User message exceeds maximum length."), NotAccepted },
    };
}
