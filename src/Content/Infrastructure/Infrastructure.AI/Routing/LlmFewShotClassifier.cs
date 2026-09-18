using System.Text.Json;
using Application.AI.Common.Interfaces.Routing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Routing;

/// <summary>
/// Shared scaffolding for the LLM few-shot classifiers in this folder
/// (<see cref="TaskComplexityClassifier"/>, <see cref="RequestIntentClassifier"/>): route to the
/// economy tier via <see cref="IModelRouter"/>, send a system/user prompt pair, parse the JSON
/// response, and fall back on any failure — a thrown exception from the model call itself, or
/// malformed/unparseable JSON. Each classifier supplies only what's genuinely different about it
/// (its prompt text and its own response shape); everything else was, before this was extracted,
/// duplicated near line-for-line between the two.
/// </summary>
internal static class LlmFewShotClassifier
{
    /// <summary>Runs one classification call. Never throws — any failure returns <paramref name="fallback"/>.</summary>
    /// <param name="modelRouter">Router used to obtain the economy-tier client for classification.</param>
    /// <param name="operationName">Passed to <see cref="IModelRouter.RouteOperationAsync"/> to resolve the tier.</param>
    /// <param name="systemPrompt">The classifier's few-shot system prompt.</param>
    /// <param name="userPrompt">The classifier's per-call user prompt.</param>
    /// <param name="parse">Extracts the classifier's own result type from the parsed JSON response.</param>
    /// <param name="fallback">Returned when the model call throws or the response can't be parsed.</param>
    /// <param name="logger">Logger for fallback warnings.</param>
    /// <param name="logSubject">Identifies the call in log output (e.g. a conversation id).</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<TResult> ClassifyAsync<TResult>(
        IModelRouter modelRouter,
        string operationName,
        string systemPrompt,
        string userPrompt,
        Func<JsonElement, TResult> parse,
        TResult fallback,
        ILogger logger,
        string logSubject,
        CancellationToken ct)
    {
        try
        {
            var routingDecision = await modelRouter.RouteOperationAsync(operationName, ct);

            var messages = new ChatMessage[]
            {
                new(ChatRole.System, systemPrompt),
                new(ChatRole.User, userPrompt)
            };

            var options = new ChatOptions
            {
                Temperature = 0.0f,
                MaxOutputTokens = 150
            };

            var response = await routingDecision.Client.GetResponseAsync(messages, options, ct);
            var responseText = response.Text?.Trim() ?? string.Empty;

            using var doc = JsonDocument.Parse(responseText);
            return parse(doc.RootElement);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to parse LLM classification response for {Subject}", logSubject);
            return fallback;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "LLM classification failed for {Subject}, falling back", logSubject);
            return fallback;
        }
    }
}
