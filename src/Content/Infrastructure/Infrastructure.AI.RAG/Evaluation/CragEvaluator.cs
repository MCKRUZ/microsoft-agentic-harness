using System.Diagnostics;
using System.Text;
using Application.AI.Common.Interfaces.RAG;
using Application.AI.Common.Interfaces.Routing;
using Application.AI.Common.StructuredOutput;
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

    // Built once — the same contract both attaches the schema to the request and validates the
    // reply against, so the two can never independently drift. See StructuredOutputSchema's remarks
    // for why its posture never marks a member required unless the CLR type does.
    private static readonly StructuredOutputContract Contract =
        StructuredOutputSchema.Build<CragResponse>("crag_evaluation", "Corrective RAG relevance evaluation");

    private readonly IModelRouter _modelRouter;
    private readonly Application.AI.Common.Interfaces.AI.IStructuredOutputInvoker _structuredOutput;
    private readonly IOptionsMonitor<AppConfig> _configMonitor;
    private readonly ILogger<CragEvaluator> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="CragEvaluator"/> class.
    /// </summary>
    /// <param name="modelRouter">Model router for selecting the standard-tier chat client.</param>
    /// <param name="structuredOutput">Schema-out, validated-parse-back, one-repair invoker.</param>
    /// <param name="configMonitor">Configuration monitor for CRAG threshold values.</param>
    /// <param name="logger">Logger for recording evaluation outcomes and failures.</param>
    public CragEvaluator(
        IModelRouter modelRouter,
        Application.AI.Common.Interfaces.AI.IStructuredOutputInvoker structuredOutput,
        IOptionsMonitor<AppConfig> configMonitor,
        ILogger<CragEvaluator> logger)
    {
        _modelRouter = modelRouter;
        _structuredOutput = structuredOutput;
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

        var routingDecision = await _modelRouter.RouteOperationAsync("crag_evaluation", cancellationToken);
        var chatClient = routingDecision.Client;
        var tier = routingDecision.SelectedTier.ToString().ToLowerInvariant();
        activity?.SetTag(RagConventions.ModelTier, tier);
        activity?.SetTag(RagConventions.ModelOperation, "crag_evaluation");

        var prompt = BuildPrompt(query, results, cragConfig);

        try
        {
            var messages = new List<ChatMessage> { new(ChatRole.User, prompt) };
            var parseResult = await _structuredOutput.InvokeAsync<CragResponse>(
                chatClient, Contract, messages, chatOptions: null, cancellationToken);

            var evaluation = BuildEvaluation(parseResult, cragConfig);

            activity?.SetTag(RagConventions.CragAction, evaluation.Action.ToString().ToLowerInvariant());
            activity?.SetTag(RagConventions.CragScore, evaluation.RelevanceScore);

            _logger.LogInformation(
                "CRAG evaluation: action={Action}, score={Score:F2}, weakChunks={WeakCount}",
                evaluation.Action, evaluation.RelevanceScore, evaluation.WeakChunkIds.Count);

            return evaluation;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A gate that cannot read its own output must never silently green-light the
            // retrieval it exists to check — EvaluationUnavailable, never Accept. See that enum
            // member's remarks; every consumer of CorrectionAction handles this explicitly.
            _logger.LogWarning(ex, "CRAG evaluation failed; the gate did not run for this retrieval");
            return UnavailableEvaluation($"Evaluation failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Converts the invoker's result into a <see cref="CragEvaluation"/>: a successful parse
    /// derives the action from the score against configured thresholds (never from the model's own
    /// <see cref="CragResponse.Action"/> label — see that type's remarks); any non-success outcome
    /// becomes <see cref="CorrectionAction.EvaluationUnavailable"/>, never a passing score.
    /// </summary>
    private static CragEvaluation BuildEvaluation(
        StructuredOutputResult<CragResponse> parseResult,
        Domain.Common.Config.AI.RAG.CragConfig cragConfig)
    {
        if (!parseResult.IsSuccess || parseResult.Value is null)
            return UnavailableEvaluation(parseResult.ErrorMessage ?? "CRAG response could not be parsed.");

        var parsed = parseResult.Value;
        var score = Math.Clamp(parsed.Score, 0.0, 1.0);
        var action = DetermineAction(score, cragConfig);

        return new CragEvaluation
        {
            Action = action,
            RelevanceScore = score,
            Reasoning = parsed.Reasoning,
            WeakChunkIds = parsed.WeakChunkIds ?? [],
        };
    }

    private static CragEvaluation UnavailableEvaluation(string reason) => new()
    {
        Action = CorrectionAction.EvaluationUnavailable,
        RelevanceScore = 0.5,
        Reasoning = reason,
    };

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
            """;
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
}
