using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Application.AI.Common.Evaluation;
using Application.AI.Common.Evaluation.Interfaces;
using Application.AI.Common.Evaluation.Models;
using Application.AI.Common.Evaluation.Outcomes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Evaluation.Judges;

/// <summary>
/// Panel-based <see cref="ILlmJudge"/> (a "jury"): scores each case with several
/// independent judges, then reduces them to a robust aggregate score plus a consensus
/// summary. Reduces the noise of a single judge and surfaces disagreement for a human.
/// </summary>
/// <remarks>
/// <para>
/// <b>Off by default.</b> When <see cref="JuryOptions.Panelists"/> is empty, this type
/// delegates straight to <see cref="DefaultLlmJudge"/> — the result is byte-identical to
/// the single-judge path (no extra calls, <see cref="LlmJudgeResult.Panel"/> stays null).
/// A panel only activates when a consumer configures one.
/// </para>
/// <para>
/// With a panel, each panelist runs in parallel through the shared
/// <see cref="JudgeCallCore"/> (same nonce-envelope injection defense as the single
/// judge), optionally against a different model and/or wearing a persona "lens". A
/// panelist whose call fails or returns malformed JSON is excluded from the aggregate
/// (its cost still counts); if every panelist fails, the original soft-fail contract is
/// preserved.
/// </para>
/// </remarks>
public sealed class JuryLlmJudge : ILlmJudge
{
    private static readonly JsonSerializerOptions PanelJsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    private readonly DefaultLlmJudge _single;
    private readonly IJudgeChatClientProvider _judgeProvider;
    private readonly IOptionsMonitor<JuryOptions> _juryOptions;
    private readonly IOptionsMonitor<JudgeOptions> _judgeOptions;
    private readonly IOptionsMonitor<JudgeCostOptions>? _costOptions;
    private readonly ILogger<JuryLlmJudge> _logger;

    /// <summary>Initializes a new instance of the <see cref="JuryLlmJudge"/> class.</summary>
    /// <param name="single">The single-judge executor used when no panel is configured.</param>
    /// <param name="judgeProvider">Resolves each panelist's chat client.</param>
    /// <param name="juryOptions">The panel configuration.</param>
    /// <param name="judgeOptions">The default judge model, used to resolve panelist model fallbacks.</param>
    /// <param name="logger">Logger for panel diagnostics.</param>
    /// <param name="costOptions">Optional per-million-token rates for USD cost computation.</param>
    public JuryLlmJudge(
        DefaultLlmJudge single,
        IJudgeChatClientProvider judgeProvider,
        IOptionsMonitor<JuryOptions> juryOptions,
        IOptionsMonitor<JudgeOptions> judgeOptions,
        ILogger<JuryLlmJudge> logger,
        IOptionsMonitor<JudgeCostOptions>? costOptions = null)
    {
        ArgumentNullException.ThrowIfNull(single);
        ArgumentNullException.ThrowIfNull(judgeProvider);
        ArgumentNullException.ThrowIfNull(juryOptions);
        ArgumentNullException.ThrowIfNull(judgeOptions);
        ArgumentNullException.ThrowIfNull(logger);

        _single = single;
        _judgeProvider = judgeProvider;
        _juryOptions = juryOptions;
        _judgeOptions = judgeOptions;
        _logger = logger;
        _costOptions = costOptions;
    }

    /// <inheritdoc />
    public async Task<LlmJudgeResult> JudgeAsync(LlmJudgeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var jury = _juryOptions.CurrentValue;
        var panelists = jury.Panelists;

        // No panel configured → identical to the single judge (same code path).
        if (panelists is null || panelists.Count == 0)
        {
            return await _single.JudgeAsync(request, cancellationToken).ConfigureAwait(false);
        }

        // Snapshot config ONCE so every panelist in this case sees the same judge model
        // and cost rates — an options reload racing Task.WhenAll must not make panelist A
        // resolve an old default deployment and panelist B a new one (a config artifact
        // would surface as a fake "conflict").
        var judge = _judgeOptions.CurrentValue;
        var cost = _costOptions?.CurrentValue;

        var panelistResults = await Task.WhenAll(
            panelists.Select(spec => RunPanelistAsync(spec, request, judge, cost, cancellationToken)))
            .ConfigureAwait(false);

        var verdicts = panelistResults
            .Select(pr => new PanelistVerdict
            {
                Name = pr.Name,
                Score = pr.Result.Score,
                Outcome = pr.Result.Outcome,
                Reasoning = pr.Result.Reasoning,
                CostUsd = pr.Result.CostUsd
            })
            .ToArray();

        var totalCost = panelistResults.Sum(pr => pr.Result.CostUsd);
        var totalInput = panelistResults.Sum(pr => pr.Result.InputTokens);
        var totalOutput = panelistResults.Sum(pr => pr.Result.OutputTokens);

        var aggregate = JuryAggregator.Aggregate(
            verdicts, jury.ScoreAggregation, jury.ConsensusMaxSpread, jury.ConflictMinSpread);

        var panelJson = JsonSerializer.Serialize(aggregate.Panel, PanelJsonOptions);

        // Every panelist failed — preserve the single-judge soft-fail contract rather than
        // emitting a Parsed result with a meaningless 0.0 score.
        if (aggregate.Panel.Responded == 0)
        {
            var outcome = verdicts.Any(v => v.Outcome == LlmJudgeOutcome.Malformed)
                ? LlmJudgeOutcome.Malformed
                : LlmJudgeOutcome.InvocationFailed;

            _logger.LogWarning(
                "Jury produced no usable scores from {Total} panelists; soft-failing as {Outcome}.",
                verdicts.Length, outcome);

            return new LlmJudgeResult
            {
                Outcome = outcome,
                Score = 0.0,
                Reasoning = $"Jury: all {verdicts.Length} panelists failed to produce a usable score.",
                RawOutput = panelJson,
                CostUsd = totalCost,
                InputTokens = totalInput,
                OutputTokens = totalOutput,
                Panel = aggregate.Panel
            };
        }

        return new LlmJudgeResult
        {
            Outcome = LlmJudgeOutcome.Parsed,
            Score = aggregate.Score,
            Reasoning = BuildSummary(aggregate, verdicts),
            RawOutput = panelJson,
            CostUsd = totalCost,
            InputTokens = totalInput,
            OutputTokens = totalOutput,
            Panel = aggregate.Panel
        };
    }

    private async Task<(string Name, LlmJudgeResult Result)> RunPanelistAsync(
        JuryPanelistSpec spec,
        LlmJudgeRequest request,
        JudgeOptions judge,
        JudgeCostOptions? cost,
        CancellationToken cancellationToken)
    {
        var name = string.IsNullOrWhiteSpace(spec.Name) ? "panelist" : spec.Name;

        var failure = JudgeCallCore.TryBuildPrompt(
            request, spec.PersonaPrompt, cost, _logger, out var systemWithNonce, out var envelopedUser);
        if (failure is not null)
        {
            return (name, failure);
        }

        IChatClient client;
        try
        {
            client = await ResolvePanelistClientAsync(spec, judge, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Panelist '{Panelist}' chat-client resolution failed.", name);
            return (name, JudgeCallCore.Failed(ex.Message, cost));
        }

        var result = await JudgeCallCore
            .InvokeAsync(client, systemWithNonce, envelopedUser, cost, _logger, cancellationToken)
            .ConfigureAwait(false);
        return (name, result);
    }

    // A panelist points at an explicit model only when both an effective client type and
    // deployment resolve (spec value, else the default JudgeOptions). Otherwise it uses the
    // default judge path (which handles first-available provider selection) — so a
    // persona-only panelist runs the configured judge with its lens.
    private Task<IChatClient> ResolvePanelistClientAsync(
        JuryPanelistSpec spec,
        JudgeOptions judge,
        CancellationToken cancellationToken)
    {
        var effClientType = spec.ClientType ?? judge.ClientType;
        var effDeployment = !string.IsNullOrWhiteSpace(spec.Deployment) ? spec.Deployment! : judge.Deployment;

        if (effClientType is { } clientType && !string.IsNullOrWhiteSpace(effDeployment))
        {
            return _judgeProvider.GetJudgeAsync(clientType, effDeployment, cancellationToken);
        }

        return _judgeProvider.GetJudgeAsync(cancellationToken);
    }

    private static string BuildSummary(JuryAggregator.JuryAggregate aggregate, IReadOnlyList<PanelistVerdict> verdicts)
    {
        var panel = aggregate.Panel;
        var bucket = panel.Bucket.ToString().ToLowerInvariant();
        var members = string.Join(
            ", ",
            verdicts.Select(v => v.Outcome == LlmJudgeOutcome.Parsed
                ? $"{v.Name}={v.Score.ToString("F2", CultureInfo.InvariantCulture)}"
                : $"{v.Name}=excluded"));

        return string.Format(
            CultureInfo.InvariantCulture,
            "Jury [{0}] score={1:F2} spread={2:F2} from {3}/{4} judges: {5}",
            bucket, aggregate.Score, panel.Spread, panel.Responded, verdicts.Count, members);
    }
}
