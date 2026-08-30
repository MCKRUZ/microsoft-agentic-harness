using Application.AI.Common.StructuredOutput;
using Microsoft.Extensions.AI;

namespace Application.AI.Common.Interfaces.AI;

/// <summary>
/// Sends a chat request with a JSON-schema hint attached, parses the reply against the target
/// type, and runs one bounded repair round-trip on a malformed first attempt.
/// </summary>
/// <remarks>
/// The one place in the codebase that owns "ask the model for JSON, validate what comes back,
/// retry once with a stricter instruction." <c>Infrastructure.AI.Planner.LlmPlanGeneratorService</c>
/// and <c>Infrastructure.AI.RAG.Evaluation.CragEvaluator</c> are its first two callers, replacing
/// hand-rolled single-shot parse-and-fail logic that had no repair attempt at all.
/// </remarks>
public interface IStructuredOutputInvoker
{
    /// <summary>
    /// Sends <paramref name="messages"/> with <paramref name="contract"/>'s schema attached as a
    /// <see cref="ChatResponseFormat"/> hint, and parses the reply as <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">The response shape.</typeparam>
    /// <param name="chatClient">The client to invoke. Not resolved by this method — the caller
    /// already knows which provider/deployment it wants.</param>
    /// <param name="contract">The contract built via <see cref="StructuredOutputSchema.Build{T}"/>.</param>
    /// <param name="messages">The conversation to send. A repair attempt appends to the system
    /// prompt rather than replaying the model's own prior output — see the implementation's remarks.</param>
    /// <param name="chatOptions">
    /// Caller-supplied options (temperature, max tokens). <see cref="ChatOptions.ResponseFormat"/>
    /// is set by this method from <paramref name="contract"/> and must not be pre-populated by the
    /// caller.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<StructuredOutputResult<T>> InvokeAsync<T>(
        IChatClient chatClient,
        StructuredOutputContract contract,
        IReadOnlyList<ChatMessage> messages,
        ChatOptions? chatOptions,
        CancellationToken cancellationToken)
        where T : class;
}
