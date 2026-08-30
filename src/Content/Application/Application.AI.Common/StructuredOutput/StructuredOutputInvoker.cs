using Application.AI.Common.Interfaces.AI;
using Application.AI.Common.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Application.AI.Common.StructuredOutput;

/// <inheritdoc cref="IStructuredOutputInvoker" />
public sealed class StructuredOutputInvoker(ILogger<StructuredOutputInvoker> logger) : IStructuredOutputInvoker
{
    // Generic — works regardless of what the caller's own system prompt said, since this layer
    // doesn't know the specific validation failure beyond "it didn't parse." Deliberately does NOT
    // say "return the same JSON object": the retry never shows the model its own prior attempt
    // (only the raw text is captured, never appended as an assistant message), so an instruction
    // implying continuity is unsatisfiable — mirrors JudgeCallCore's identical reasoning.
    private const string MalformedJsonAddendum =
        "Your previous reply was not valid JSON, or was missing a required field. You MUST return " +
        "exactly one JSON object matching the requested schema, no fences, no commentary, and every " +
        "required field populated.";

    /// <inheritdoc />
    public async Task<StructuredOutputResult<T>> InvokeAsync<T>(
        IChatClient chatClient,
        StructuredOutputContract contract,
        IReadOnlyList<ChatMessage> messages,
        ChatOptions? chatOptions,
        CancellationToken cancellationToken)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(chatClient);
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(messages);

        if (contract.ResponseType != typeof(T))
            throw new ArgumentException(
                $"Contract is for '{contract.ResponseType.Name}' but InvokeAsync<{typeof(T).Name}> was called.",
                nameof(contract));

        var responseFormat = ChatResponseFormat.ForJsonSchema(contract.Schema, contract.SchemaName, contract.SchemaDescription);
        var options = chatOptions?.Clone() ?? new ChatOptions();
        options.ResponseFormat = responseFormat;

        string? lastRaw = null;
        string? retryAddendum = null;

        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var attemptMessages = BuildMessages(messages, retryAddendum);
                var response = await chatClient
                    .GetResponseAsync(attemptMessages, options, cancellationToken)
                    .ConfigureAwait(false);
                lastRaw = response.Text ?? string.Empty;

                if (string.IsNullOrWhiteSpace(lastRaw))
                {
                    // An empty body isn't a JSON-format problem; a stricter format instruction
                    // can't fix "the model said nothing" — abort the retry budget early, matching
                    // JudgeCallCore's identical reasoning for the same case.
                    logger.LogWarning(
                        "Structured output call for '{Schema}' returned an empty body on attempt {Attempt}; skipping repair.",
                        contract.SchemaName, attempt + 1);
                    return StructuredOutputResult<T>.Fail(
                        StructuredOutcome.EmptyResponse, "Model returned an empty response.", lastRaw);
                }

                if (LlmJsonResponseParser.TryParseObject<T>(lastRaw, contract.SerializerOptions, out var parsed) && parsed is not null)
                    return StructuredOutputResult<T>.Success(parsed, lastRaw);

                logger.LogWarning(
                    "Structured output call for '{Schema}' returned malformed output on attempt {Attempt}.",
                    contract.SchemaName, attempt + 1);
                retryAddendum = MalformedJsonAddendum;
            }

            var outcome = retryAddendum is null ? StructuredOutcome.Malformed : StructuredOutcome.RepairFailed;
            return StructuredOutputResult<T>.Fail(
                outcome, $"Model did not return valid '{contract.SchemaName}' JSON after a repair attempt.", lastRaw);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Structured output invocation failed for '{Schema}'.", contract.SchemaName);
            return StructuredOutputResult<T>.Fail(
                StructuredOutcome.InvocationFailed, $"Invocation failed: {ex.Message}", lastRaw);
        }
    }

    private static IList<ChatMessage> BuildMessages(IReadOnlyList<ChatMessage> messages, string? retryAddendum)
    {
        if (retryAddendum is null) return [.. messages];

        // Appended as an additional system message rather than mutating the caller's original
        // system message text, and never as an assistant message replaying the prior (bad) output
        // — see the class remarks on MalformedJsonAddendum.
        return [.. messages, new ChatMessage(ChatRole.System, retryAddendum)];
    }
}
