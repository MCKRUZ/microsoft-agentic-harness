using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Application.AI.Common.Interfaces.Routing;
using Application.AI.Common.Prompts.Exceptions;
using Application.AI.Common.Prompts.Interfaces;
using Domain.AI.KnowledgeGraph.Models;
using Domain.AI.Prompts;
using Domain.Common.Config.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.KnowledgeGraph.Memory;

/// <summary>
/// Extracts structured facts from conversation turns using an economy-tier LLM.
/// The fact-extraction prompt is resolved from the versioned <see cref="IPromptRegistry"/>
/// (<c>conversation-fact-extractor</c>) and rendered with <see cref="IPromptRenderer"/>
/// so trace-replay can recover which prompt version produced each fact set. Catches
/// all expected failures internally — callers always receive a valid (possibly empty)
/// list.
/// </summary>
public sealed class ConversationFactExtractor : IConversationFactExtractor
{
    private const string PromptName = "conversation-fact-extractor";
    private const string MetricKey = "fact_extraction";
    private const int AssistantResponseTruncationLimit = 2000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IModelRouter _modelRouter;
    private readonly IPromptRegistry _promptRegistry;
    private readonly IPromptRenderer _promptRenderer;
    private readonly IPromptUsageRecorder _usageRecorder;
    private readonly IOptionsMonitor<KnowledgeBridgeConfig> _config;
    private readonly ILogger<ConversationFactExtractor> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConversationFactExtractor"/> class.
    /// </summary>
    /// <param name="modelRouter">Routes LLM calls to the appropriate model tier.</param>
    /// <param name="promptRegistry">Versioned prompt registry; resolves the fact-extractor template.</param>
    /// <param name="promptRenderer">Renders the resolved template with variable substitution (Scriban).</param>
    /// <param name="usageRecorder">Stamps OTel / persists which prompt version was used per turn.</param>
    /// <param name="config">Live-reloadable knowledge-bridge configuration; <see cref="KnowledgeBridgeConfig.MinConfidence"/>
    /// is read fresh on every extraction so an operator can retune the threshold without a redeploy.</param>
    /// <param name="logger">Logger for recording extraction results and failures.</param>
    public ConversationFactExtractor(
        IModelRouter modelRouter,
        IPromptRegistry promptRegistry,
        IPromptRenderer promptRenderer,
        IPromptUsageRecorder usageRecorder,
        IOptionsMonitor<KnowledgeBridgeConfig> config,
        ILogger<ConversationFactExtractor> logger)
    {
        ArgumentNullException.ThrowIfNull(modelRouter);
        ArgumentNullException.ThrowIfNull(promptRegistry);
        ArgumentNullException.ThrowIfNull(promptRenderer);
        ArgumentNullException.ThrowIfNull(usageRecorder);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);

        _modelRouter = modelRouter;
        _promptRegistry = promptRegistry;
        _promptRenderer = promptRenderer;
        _usageRecorder = usageRecorder;
        _config = config;
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
        PromptDescriptor descriptor;
        try
        {
            descriptor = await _promptRegistry.GetLatestAsync(PromptName, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is KeyNotFoundException or PromptRegistryUnavailableException)
        {
            _logger.LogWarning(ex,
                "Could not resolve prompt '{Prompt}' for conversation {ConversationId} turn {Turn}; returning no facts.",
                PromptName, conversationId, turnNumber);
            return [];
        }

        try
        {
            var truncatedResponse = assistantResponse[..Math.Min(assistantResponse.Length, AssistantResponseTruncationLimit)];
            var variables = new Dictionary<string, object?>
            {
                ["user_message"] = userMessage,
                ["assistant_message"] = truncatedResponse,
            };
            var rendered = await _promptRenderer.RenderAsync(descriptor, variables, cancellationToken).ConfigureAwait(false);

            await _usageRecorder.RecordAsync(
                descriptor,
                new PromptUsageContext
                {
                    CaseId = string.Create(CultureInfo.InvariantCulture, $"{conversationId}:{turnNumber}"),
                    MetricKey = MetricKey,
                },
                cancellationToken).ConfigureAwait(false);

            var client = (await _modelRouter.RouteOperationAsync("fact_extraction", cancellationToken)).Client;
            var response = await client.GetResponseAsync(rendered.Body, cancellationToken: cancellationToken);

            var json = response.Text ?? "[]";
            var facts = ParseFacts(json, conversationId, turnNumber, _config.CurrentValue.MinConfidence);

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

    private static IReadOnlyList<ConversationFact> ParseFacts(
        string json, string conversationId, int turnNumber, double minConfidence)
    {
        if (!Application.AI.Common.Json.LlmJsonResponseParser.TryParseArray<List<RawFact>>(json, JsonOptions, out var rawFacts)
            || rawFacts is null or { Count: 0 })
        {
            return [];
        }

        var factIndex = 0;
        return rawFacts
            .Where(f => f.Confidence >= minConfidence)
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
