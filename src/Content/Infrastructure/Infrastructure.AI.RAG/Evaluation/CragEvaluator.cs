using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Application.AI.Common.Interfaces.RAG;
using Domain.AI.RAG.Enums;
using Domain.AI.RAG.Models;
using Domain.AI.Telemetry.Conventions;
using Domain.Common.Config;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.RAG.Evaluation;

/// <summary>
/// Evaluates retrieval quality using the Corrective RAG (CRAG) pattern.
/// Sends the query and top retrieved chunks to a standard-tier LLM, which scores
/// overall relevance (0-1) and determines a correction action based on configured
/// thresholds. Chunks identified as weak are returned in
/// <see cref="CragEvaluation.WeakChunkIds"/> so the assembler can exclude them.
/// </summary>
public sealed class CragEvaluator : ICragEvaluator
{
    private static readonly ActivitySource ActivitySource = new("AgenticHarness.RAG.Evaluation");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IRagModelRouter _modelRouter;
    private readonly IOptionsMonitor<AppConfig> _configMonitor;
    private readonly ILogger<CragEvaluator> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="CragEvaluator"/> class.
    /// </summary>
    /// <param name="modelRouter">Model router for selecting the standard-tier chat client.</param>
    /// <param name="configMonitor">Configuration monitor for CRAG threshold values.</param>
    /// <param name="logger">Logger for recording evaluation outcomes and failures.</param>
    public CragEvaluator(
        IRagModelRouter modelRouter,
        IOptionsMonitor<AppConfig> configMonitor,
        ILogger<CragEvaluator> logger)
    {
        _modelRouter = modelRouter;
        _configMonitor = configMonitor;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<CragEvaluation> EvaluateAsync(
        string query,
        IReadOnlyList<RetrievalResult> results,
        CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("rag.crag.evaluate");
        var cragConfig = _configMonitor.CurrentValue.AI.Rag.Crag;

        var chatClient = _modelRouter.GetClientForOperation("crag_evaluation");
        var tier = _modelRouter.GetTierForOperation("crag_evaluation");
        activity?.SetTag(RagConventions.ModelTier, tier);
        activity?.SetTag(RagConventions.ModelOperation, "crag_evaluation");

        var prompt = BuildPrompt(query, results, cragConfig);

        try
        {
            var response = await chatClient.GetResponseAsync(prompt, cancellationToken: cancellationToken);
            var evaluation = ParseResponse(response.Text ?? "{}", cragConfig);

            activity?.SetTag(RagConventions.CragAction, evaluation.Action.ToString().ToLowerInvariant());
            activity?.SetTag(RagConventions.CragScore, evaluation.RelevanceScore);

            _logger.LogInformation(
                "CRAG evaluation: action={Action}, score={Score:F2}, weakChunks={WeakCount}",
                evaluation.Action, evaluation.RelevanceScore, evaluation.WeakChunkIds.Count);

            return evaluation;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CRAG evaluation failed; defaulting to Accept to avoid blocking pipeline");
            return new CragEvaluation
            {
                Action = CorrectionAction.Accept,
                RelevanceScore = 0.5,
                Reasoning = $"Evaluation failed: {ex.Message}"
            };
        }
    }

    private static string BuildPrompt(
        string query,
        IReadOnlyList<RetrievalResult> results,
        Domain.Common.Config.AI.RAG.CragConfig cragConfig)
    {
        var passages = new StringBuilder();
        for (var i = 0; i < results.Count; i++)
        {
            var chunk = results[i].Chunk;
            passages.AppendLine($"[{i + 1}] (id: {chunk.Id}) {chunk.Content}");
        }

        return $$"""
            Evaluate whether these retrieved passages are relevant to the query.

            Query: {{query}}

            Passages:
            {{passages}}

            Rate overall relevance 0.0-1.0 and determine action:
            - "Accept" if score >= {{cragConfig.AcceptThreshold}}
            - "Refine" if score >= {{cragConfig.RefineThreshold}} but < {{cragConfig.AcceptThreshold}}
            - "Reject" if score < {{cragConfig.RefineThreshold}}

            Also list IDs of any weak/irrelevant passages.

            Respond as JSON: {"action": "...", "score": 0.0, "reasoning": "...", "weak_chunk_ids": [...]}
            """;
    }

    private CragEvaluation ParseResponse(
        string responseText,
        Domain.Common.Config.AI.RAG.CragConfig cragConfig)
    {
        try
        {
            var jsonStart = responseText.IndexOf('{');
            var jsonEnd = responseText.LastIndexOf('}');
            if (jsonStart < 0 || jsonEnd < 0)
                return FallbackEvaluation("No JSON found in response");

            var json = responseText[jsonStart..(jsonEnd + 1)];
            var parsed = JsonSerializer.Deserialize<CragResponseDto>(json, JsonOptions);
            if (parsed is null)
                return FallbackEvaluation("Failed to deserialize CRAG response");

            var score = Math.Clamp(parsed.Score, 0.0, 1.0);
            var action = DetermineAction(score, cragConfig);

            return new CragEvaluation
            {
                Action = action,
                RelevanceScore = score,
                Reasoning = parsed.Reasoning,
                WeakChunkIds = parsed.WeakChunkIds ?? []
            };
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse CRAG JSON response");
            return FallbackEvaluation("JSON parse error");
        }
    }

    private static CorrectionAction DetermineAction(
        double score,
        Domain.Common.Config.AI.RAG.CragConfig cragConfig)
    {
        if (score >= cragConfig.AcceptThreshold)
            return CorrectionAction.Accept;
        if (score >= cragConfig.RefineThreshold)
            return CorrectionAction.Refine;
        return cragConfig.AllowWebFallback
            ? CorrectionAction.WebFallback
            : CorrectionAction.Reject;
    }

    private static CragEvaluation FallbackEvaluation(string reason) =>
        new()
        {
            Action = CorrectionAction.Accept,
            RelevanceScore = 0.5,
            Reasoning = $"Fallback: {reason}"
        };

    /// <summary>DTO for deserializing the LLM's JSON response.</summary>
    private sealed class CragResponseDto
    {
        public string? Action { get; set; }
        public double Score { get; set; }
        public string? Reasoning { get; set; }
        public List<string>? WeakChunkIds { get; set; }
    }
}
