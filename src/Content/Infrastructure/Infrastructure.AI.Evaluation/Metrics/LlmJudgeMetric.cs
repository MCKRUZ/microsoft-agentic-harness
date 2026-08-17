using System.Diagnostics;
using Application.AI.Common.Evaluation.Governance;
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
    //
    // Each opt-in toggles one named block on or off in BuildUserPromptTemplate — never
    // string surgery on a rendered template. Composing RubricBlock + InputBlock +
    // ExpectedOutputBlock + AssistantOutputBlock, in that order, reproduces the template
    // this class shipped with byte-for-byte; that identity is pinned by a test.
    private const string RubricBlock = "<rubric>\n{{rubric}}\n</rubric>";
    private const string InputBlock = "<case_input>\n{{input}}\n</case_input>";
    private const string ExpectedOutputBlock = "<expected_output>\n{{expected_output}}\n</expected_output>";
    private const string AssistantOutputBlock = "<assistant_output>\n{{output}}\n</assistant_output>";
    private const string ToolsInvokedBlock = "<tools_invoked>\n{{tools_invoked}}\n</tools_invoked>";
    private const string GovernanceBlock = "<governance_trace>\n{{governance}}\n</governance_trace>";

    private const string SystemPromptLegacy =
        "You are an evaluation judge. Score the assistant's response against the rubric. " +
        "Respond ONLY with a single JSON object: {\"score\": <0.0-1.0>, \"reasoning\": \"<one or two sentences>\"}. " +
        "Do not include markdown fences, prose, or any text outside the JSON object.";

    private const string SystemPromptStrict =
        "You are an evaluation judge. Score the assistant's response against the rubric. " +
        "Respond ONLY with a single JSON object: {\"score\": <0.0-1.0>, \"reasoning\": \"<one or two sentences>\", " +
        "\"violated_clause\": \"<required on a failing score: the exact sentence copied character-for-character " +
        "from the rubric that the response violates; omit or leave empty on a passing score>\", " +
        "\"evidence\": [\"<optional: a tool name or quoted span of the assistant's output supporting the score>\"]}. " +
        "Do not include markdown fences, prose, or any text outside the JSON object. If you fail the response, " +
        "you MUST quote the specific rubric requirement it violates verbatim in \"violated_clause\" — do not " +
        "paraphrase or invent a requirement that is not present in the rubric.";

    private const string VerdictContractKey = "verdict_contract";
    private const string TrajectoryKey = "trajectory";
    private const string IncludeExpectedOutputKey = "include_expected_output";

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

        var options = ParseOptions(spec);

        // Deterministic, zero-token guard: a rubric that declared a governance dependency
        // and got nothing must not silently score as if the agent complied. This runs
        // before the judge is ever called — the whole point of the strict contract is that
        // we stopped trusting the judge to police itself, so it isn't asked to here either.
        if (options.IncludeGovernance && !GovernanceTraceRenderer.IsEngaged(output.Governance))
        {
            return Warn(sw,
                "Rubric declares a governance-trace dependency (trajectory: governance) but no governance " +
                "decisions were recorded for this run — the verdict cannot be graded.");
        }

        // Note: previously accepted a 'system' parameter that appended to the system
        // prompt. Removed — case-author input is untrusted; appending it to the
        // system role would let crafted cases coerce arbitrary scores. The rubric
        // (also case-authored) is safely positioned inside the user-data envelope.
        var request = new LlmJudgeRequest
        {
            SystemPromptCore = options.Strict ? SystemPromptStrict : SystemPromptLegacy,
            UserPromptTemplate = BuildUserPromptTemplate(
                options.IncludeExpectedOutput, options.IncludeTools, options.IncludeGovernance),
            Variables = BuildVariables(rubric, @case, output, options),
            VerdictContract = options.Strict
                ? new JudgeVerdictContract { ClauseSource = rubric, FailingBelow = spec.Threshold }
                : null,
        };

        LlmJudgeResult result;
        try
        {
            result = await _judge.JudgeAsync(request, cancellationToken).ConfigureAwait(false);
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

    /// <summary>The three opt-in toggles parsed from a case's <see cref="MetricSpec.Parameters"/>.</summary>
    private readonly record struct ParsedOptions(
        bool Strict, bool IncludeExpectedOutput, bool IncludeTools, bool IncludeGovernance);

    private ParsedOptions ParseOptions(MetricSpec spec)
    {
        var (tools, governance) = ParseTrajectory(spec);
        return new ParsedOptions(ParseVerdictContractStrict(spec), ParseIncludeExpectedOutput(spec), tools, governance);
    }

    private static Dictionary<string, string?> BuildVariables(
        string rubric, EvalCase @case, AgentInvocationResult output, ParsedOptions options)
    {
        var variables = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["rubric"] = rubric,
            ["input"] = @case.Input,
            ["output"] = output.Output,
        };
        if (options.IncludeExpectedOutput)
        {
            variables["expected_output"] = @case.ExpectedOutput ?? "(not provided)";
        }
        if (options.IncludeTools)
        {
            variables["tools_invoked"] = RenderToolsInvoked(output.ToolsInvoked);
        }
        if (options.IncludeGovernance)
        {
            variables["governance"] = GovernanceTraceRenderer.Render(output.Governance);
        }
        return variables;
    }

    /// <summary>
    /// Composes the user-prompt template from named blocks in a fixed order (rubric, input,
    /// [expected], output, [tools], [governance]) so each opt-in is a block toggle, not
    /// string surgery on a rendered template. With every opt-in off this reproduces the
    /// template this class has always used, character-for-character.
    /// </summary>
    private static string BuildUserPromptTemplate(bool includeExpectedOutput, bool includeTools, bool includeGovernance)
    {
        var blocks = new List<string> { RubricBlock, InputBlock };
        if (includeExpectedOutput)
        {
            blocks.Add(ExpectedOutputBlock);
        }
        blocks.Add(AssistantOutputBlock);
        if (includeTools)
        {
            blocks.Add(ToolsInvokedBlock);
        }
        if (includeGovernance)
        {
            blocks.Add(GovernanceBlock);
        }
        return string.Join("\n\n", blocks);
    }

    private static string RenderToolsInvoked(IReadOnlyList<string> toolsInvoked)
        => toolsInvoked.Count == 0
            ? "(no tools were invoked)"
            : string.Join("\n", toolsInvoked.Select((tool, i) => $"{i + 1}. {tool}"));

    /// <summary>
    /// Fail-soft: an unrecognized value defaults to the legacy contract with a warning
    /// rather than throwing — a typo in a case's YAML must not take down the whole eval run.
    /// </summary>
    private bool ParseVerdictContractStrict(MetricSpec spec)
    {
        if (!spec.Parameters.TryGetValue(VerdictContractKey, out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            "strict" => true,
            "legacy" => false,
            _ => LogUnknownVerdictContractAndDefault(raw)
        };
    }

    private bool LogUnknownVerdictContractAndDefault(string raw)
    {
        _logger.LogWarning(
            "Unknown {Key} value '{Value}' in an llm_judge case; defaulting to legacy.", VerdictContractKey, raw);
        return false;
    }

    /// <summary>Fail-soft: an unparseable value defaults to today's behaviour (included).</summary>
    private bool ParseIncludeExpectedOutput(MetricSpec spec)
    {
        if (!spec.Parameters.TryGetValue(IncludeExpectedOutputKey, out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        if (bool.TryParse(raw, out var value))
        {
            return value;
        }

        _logger.LogWarning(
            "Unparseable {Key} value '{Value}' in an llm_judge case; defaulting to true.",
            IncludeExpectedOutputKey, raw);
        return true;
    }

    /// <summary>Fail-soft: an unrecognized token is logged and ignored, not thrown on.</summary>
    private (bool Tools, bool Governance) ParseTrajectory(MetricSpec spec)
    {
        if (!spec.Parameters.TryGetValue(TrajectoryKey, out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return (false, false);
        }

        var tools = false;
        var governance = false;
        foreach (var token in raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (token.ToLowerInvariant())
            {
                case "tools":
                    tools = true;
                    break;
                case "governance":
                    governance = true;
                    break;
                default:
                    _logger.LogWarning(
                        "Unknown {Key} token '{Token}' in an llm_judge case; ignoring.", TrajectoryKey, token);
                    break;
            }
        }

        return (tools, governance);
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
