namespace Application.AI.Common.Exceptions;

/// <summary>
/// An exception the request-screening pipeline throws when it refuses a turn: the content was blocked
/// (<see cref="ContentSafetyException"/>), could not be screened (<see cref="ContentSafetyUnavailableException"/>), or
/// matched a prompt-injection pattern (<see cref="PromptInjectionBlockedException"/>).
/// </summary>
/// <remarks>
/// A transport that stores the caller's message before the turn runs must remove it again when the turn is refused
/// (#959): the next turn reads its history back from the store unscreened, so a stored refusal would reach the model as
/// soon as the caller sent anything that passes. A refusal is also not the agent failing, so it is not counted against
/// the agent's health.
/// </remarks>
public interface IScreeningRefusal
{
    /// <summary>What the caller is told. Never contains the screened text.</summary>
    string ClientMessage { get; }
}
