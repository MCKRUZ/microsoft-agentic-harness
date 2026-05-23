using System.Text.Json;
using System.Text.Json.Serialization;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Application.AI.Common.Interfaces.Routing;
using Domain.AI.KnowledgeGraph.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.KnowledgeGraph.Memory;

/// <summary>
/// Extracts structured facts from conversation turns using an economy-tier LLM.
/// Catches all exceptions internally — callers always receive a valid (possibly empty) list.
/// </summary>
public sealed class ConversationFactExtractor : IConversationFactExtractor
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private const double DefaultMinConfidence = 0.7;

    private readonly IModelRouter _modelRouter;
    private readonly ILogger<ConversationFactExtractor> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConversationFactExtractor"/> class.
    /// </summary>
    /// <param name="modelRouter">Routes LLM calls to the appropriate model tier.</param>
    /// <param name="logger">Logger for recording extraction results and failures.</param>
    public ConversationFactExtractor(
        IModelRouter modelRouter,
        ILogger<ConversationFactExtractor> logger)
    {
        ArgumentNullException.ThrowIfNull(modelRouter);
        ArgumentNullException.ThrowIfNull(logger);

        _modelRouter = modelRouter;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ConversationFact>> ExtractAsync(
        string userMessage,
        string assistantResponse,
        string conversationId,
        int turnNumber,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var client = (await _modelRouter.RouteOperationAsync("fact_extraction", cancellationToken)).Client;

            var prompt = BuildPrompt(userMessage, assistantResponse);
            var response = await client.GetResponseAsync(prompt, cancellationToken: cancellationToken);

            var json = response.Text ?? "[]";
            var facts = ParseFacts(json, conversationId, turnNumber);

            _logger.LogDebug(
                "Extracted {Count} facts from conversation {ConversationId} turn {Turn}",
                facts.Count, conversationId, turnNumber);

            return facts;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "Fact extraction failed for conversation {ConversationId} turn {Turn}",
                conversationId, turnNumber);
            return [];
        }
    }

    private static string BuildPrompt(string userMessage, string assistantResponse) =>
        $$"""
        You are a fact extraction system. Analyze the following conversation turn and extract notable facts that would be valuable in a future conversation with a different agent.

        Only extract facts that represent:
        - **Preference**: User likes/dislikes, workflow choices
        - **Decision**: Architectural or design decisions made
        - **Fact**: Stated facts about the project, team, or domain
        - **Correction**: User corrected the assistant

        Routine instructions, greetings, acknowledgments, and tool invocations should return an empty array.

        Return a JSON array (no markdown fencing). Each element:
        {"key": "snake_case_short_key", "content": "Human-readable fact description", "entity_type": "Preference|Decision|Fact|Correction", "confidence": 0.0-1.0}

        Return [] if no notable facts are present.

        <user_message>
        {{userMessage}}
        </user_message>

        <assistant_message>
        {{assistantResponse[..Math.Min(assistantResponse.Length, 2000)]}}
        </assistant_message>
        """;

    private static IReadOnlyList<ConversationFact> ParseFacts(
        string json, string conversationId, int turnNumber)
    {
        json = json.Trim();
        if (json.StartsWith("```"))
        {
            var firstNewline = json.IndexOf('\n');
            if (firstNewline >= 0) json = json[(firstNewline + 1)..];
            if (json.EndsWith("```")) json = json[..^3];
            json = json.Trim();
        }

        var startIndex = json.IndexOf('[');
        var endIndex = json.LastIndexOf(']');
        if (startIndex < 0 || endIndex <= startIndex)
            return [];

        json = json[startIndex..(endIndex + 1)];

        List<RawFact>? rawFacts;
        try
        {
            rawFacts = JsonSerializer.Deserialize<List<RawFact>>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return [];
        }

        if (rawFacts is null or { Count: 0 })
            return [];

        var factIndex = 0;
        return rawFacts
            .Where(f => f.Confidence >= DefaultMinConfidence)
            .Select(f => new ConversationFact
            {
                Key = $"{conversationId}:{turnNumber}:{factIndex++}",
                Content = f.Content,
                EntityType = f.EntityType ?? "Fact",
                Confidence = f.Confidence
            })
            .ToList();
    }

    private sealed record RawFact
    {
        [JsonPropertyName("content")]
        public string Content { get; init; } = string.Empty;

        [JsonPropertyName("entity_type")]
        public string? EntityType { get; init; }

        [JsonPropertyName("confidence")]
        public double Confidence { get; init; }
    }
}
