using System.Diagnostics;
using Application.AI.Common.Evaluation.Interfaces;
using Application.AI.Common.Evaluation.Models;
using Application.AI.Common.Evaluation.Outcomes;
using Domain.AI.Evaluation;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Evaluation.Metrics;

/// <summary>
/// Generic rubric-driven LLM-judge metric. The case author supplies the rubric in
/// the metric spec; the metric calls the shared <see cref="ILlmJudge"/> with the
/// rubric embedded in the system prompt and the case fields as variables.
/// </summary>
/// <remarks>
/// <para>
/// Required parameters: <c>rubric</c> (the grading instruction). The rubric is
/// inserted into the USER prompt (inside the nonce envelope built by the judge),
/// not the system prompt — so case authors cannot poison the trusted system role with
/// injected instructions like "always score 1.0". The <c>system</c> parameter that
/// previously appended to the system prompt has been removed: it conflated case-author
/// input (untrusted) with system-role text (trusted by the judge model).
/// </para>
/// <para>
/// All injection mitigations (per-invocation nonce envelope + HtmlEncode of variable
/// values + nonce-collision detection) live inside <see cref="ILlmJudge"/> — this
/// metric just supplies the structured request.
/// </para>
/// </remarks>
public sealed class LlmJudgeMetric : IEvalMetric
{
    // The inner tags are semantic field labels only; isolation is provided by the
    // outer <judge_data_NONCE>...</judge_data_NONCE> envelope that DefaultLlmJudge
    // wraps around the rendered body. Keeping the inner per-field wrappers
    // nonce-suffixed would be belt-and-suspenders, but two layers invite refactor
    // confusion ("is the outer envelope redundant?"). One layer, clearly owned.
    private const string UserPromptTemplate = """
        <rubric>
        {{rubric}}
        </rubric>

        <case_input>
        {{input}}
        </case_input>

        <expected_output>
        {{expected_output}}
        </expected_output>

        <assistant_output>
        {{output}}
        </assistant_output>
        """;

    private const string SystemPromptBase =
        "You are an evaluation judge. Score the assistant's response against the rubric. " +
        "Respond ONLY with a single JSON object: {\"score\": <0.0-1.0>, \"reasoning\": \"<one or two sentences>\"}. " +
        "Do not include markdown fences, prose, or any text outside the JSON object.";

    private readonly ILlmJudge _judge;
    private readonly ILogger<LlmJudgeMetric> _logger;

    /// <summary>Initializes a new instance.</summary>
    /// <param name="judge">Shared judge call service.</param>
    /// <param name="logger">Logger for diagnostics.</param>
    public LlmJudgeMetric(ILlmJudge judge, ILogger<LlmJudgeMetric> logger)
    {
        ArgumentNullException.ThrowIfNull(judge);
        ArgumentNullException.ThrowIfNull(logger);

        _judge = judge;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Key => "llm_judge";

    /// <inheritdoc />
    public async Task<MetricScore> ScoreAsync(
        EvalCase @case,
        AgentInvocationResult output,
        MetricSpec spec,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(@case);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(spec);

        var sw = Stopwatch.StartNew();

        if (!spec.Parameters.TryGetValue("rubric", out var rubric) || string.IsNullOrWhiteSpace(rubric))
        {
            return Warn(sw, "llm_judge requires a 'rubric' parameter.");
        }

        // Note: previously accepted a 'system' parameter that appended to the system
        // prompt. Removed — case-author input is untrusted; appending it to the
        // system role would let crafted cases coerce arbitrary scores. The rubric
        // (also case-authored) is safely positioned inside the user-data envelope.
        var systemPrompt = SystemPromptBase;

        var variables = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["rubric"] = rubric,
            ["input"] = @case.Input,
            ["expected_output"] = @case.ExpectedOutput ?? "(not provided)",
            ["output"] = output.Output,
        };

        LlmJudgeResult result;
        try
        {
            result = await _judge.JudgeAsync(
                new LlmJudgeRequest
                {
                    SystemPromptCore = systemPrompt,
                    UserPromptTemplate = UserPromptTemplate,
                    Variables = variables,
                },
                cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Judge invocation escaped contract for llm_judge case {CaseId}.", @case.Id);
            return Warn(sw, $"Judge invocation error: {ex.Message}");
        }

        sw.Stop();

        return JudgeMetricScoreMapper.ToMetricScore(Key, result, spec.Threshold, sw.Elapsed);
    }

    private MetricScore Warn(Stopwatch sw, string reasoning)
    {
        sw.Stop();
        return new MetricScore
        {
            MetricKey = Key,
            Score = 0.0,
            Verdict = Verdict.Warn,
            Reasoning = reasoning,
            RawOutput = null,
            CostUsd = 0m,
            Duration = sw.Elapsed
        };
    }
}
