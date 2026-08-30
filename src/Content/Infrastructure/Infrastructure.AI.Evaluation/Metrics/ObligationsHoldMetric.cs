using System.Diagnostics;
using Application.AI.Common.Evaluation.Interfaces;
using Application.AI.Common.Evaluation.Models;
using Application.AI.Common.Interfaces.Verification;
using Application.AI.Common.Services.Verification;
using Domain.AI.Evaluation;
using Domain.AI.Verification;
using Domain.Common.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Evaluation.Metrics;

/// <summary>
/// Scores an eval case's output by extracting obligations from it (<see cref="IObligationExtractor"/>)
/// and verifying each one against the same output (<see cref="ObligationVerificationRunner"/>) —
/// the free consumption seam obligation-based analysis (#320) gives the eval framework and, through
/// it, Package G's per-skill eval sets and the training loop.
/// </summary>
/// <remarks>
/// Fail-soft per <see cref="IEvalMetric"/>'s own remarks: extraction failure, a disabled feature
/// flag, or no output at all produce <see cref="Verdict.Warn"/>, never a thrown exception and never
/// a false <see cref="Verdict.Pass"/>. An empty obligation list — the artifact had nothing to check
/// — is a genuine <see cref="Verdict.Pass"/>, not a warning; see <see cref="IObligationExtractor"/>'s
/// remarks for why extraction failure and "found nothing" must stay distinguishable.
/// </remarks>
public sealed class ObligationsHoldMetric : IEvalMetric
{
    private readonly IObligationExtractor _extractor;
    private readonly ObligationVerificationRunner _runner;
    private readonly IOptionsMonitor<AppConfig> _config;
    private readonly ILogger<ObligationsHoldMetric> _logger;

    /// <inheritdoc />
    public string Key => "obligations_hold";

    /// <summary>Initializes a new instance of the <see cref="ObligationsHoldMetric"/> class.</summary>
    public ObligationsHoldMetric(
        IObligationExtractor extractor,
        ObligationVerificationRunner runner,
        IOptionsMonitor<AppConfig> config,
        ILogger<ObligationsHoldMetric> logger)
    {
        ArgumentNullException.ThrowIfNull(extractor);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);

        _extractor = extractor;
        _runner = runner;
        _config = config;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<MetricScore> ScoreAsync(
        EvalCase @case,
        AgentInvocationResult output,
        MetricSpec spec,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();

        if (!_config.CurrentValue.AI.Obligations.Enabled)
        {
            sw.Stop();
            return Warn(sw, "Obligation verification is disabled (AI:Obligations:Enabled=false).");
        }

        if (!output.Success || string.IsNullOrWhiteSpace(output.Output))
        {
            sw.Stop();
            return Warn(sw, "No output to extract obligations from.");
        }

        // IObligationExtractor.ExtractAsync throws ArgumentException on a blank artifactPath.
        // EvalCase.Id is required but that only guarantees it's set, not non-blank — a dataset
        // supplying "id": "" would otherwise throw out of a metric whose own remarks promise it
        // never does.
        if (string.IsNullOrWhiteSpace(@case.Id))
        {
            sw.Stop();
            return Warn(sw, "EvalCase.Id is blank; cannot extract obligations without an artifact identifier.");
        }

        var extraction = await _extractor.ExtractAsync(@case.Id, output.Output, cancellationToken).ConfigureAwait(false);
        if (!extraction.IsSuccess || extraction.Value is null)
        {
            sw.Stop();
            _logger.LogWarning(
                "Obligation extraction failed for case '{CaseId}': {Errors}", @case.Id, string.Join("; ", extraction.Errors));
            return Warn(sw, $"Obligation extraction failed: {string.Join("; ", extraction.Errors)}");
        }

        if (extraction.Value.Count == 0)
        {
            sw.Stop();
            return new MetricScore
            {
                MetricKey = Key,
                Score = 1.0,
                Verdict = Verdict.Pass,
                Reasoning = "No obligations found — nothing to check.",
                Duration = sw.Elapsed
            };
        }

        var verdicts = await _runner.RunAsync(extraction.Value, output.Output, cancellationToken).ConfigureAwait(false);
        var broken = verdicts.Where(v => v.Outcome == VerificationOutcome.Broken).ToList();

        // verdicts.Count reflects only what the runner actually dispatched — after
        // ObligationValidator rejects malformed obligations and MaxObligations caps the rest —
        // not extraction.Value.Count, the original extracted total. Reporting the gap keeps the
        // reasoning honest about coverage rather than implying every extracted obligation was
        // checked when some may have been silently rejected or dropped.
        var notDispatched = extraction.Value.Count - verdicts.Count;
        var coverageNote = notDispatched > 0
            ? $" ({notDispatched} extracted obligation(s) not dispatched — rejected as malformed or over the configured cap.)"
            : string.Empty;

        sw.Stop();
        if (broken.Count > 0)
        {
            var summary = string.Join(" | ", broken.Select(v => $"{v.Obligation.Property}: {v.Explanation}"));
            return new MetricScore
            {
                MetricKey = Key,
                Score = 0.0,
                Verdict = Verdict.Fail,
                Reasoning = $"{broken.Count}/{verdicts.Count} dispatched obligation(s) broken: {summary}{coverageNote}",
                Duration = sw.Elapsed
            };
        }

        return new MetricScore
        {
            MetricKey = Key,
            Score = 1.0,
            Verdict = Verdict.Pass,
            Reasoning = $"All {verdicts.Count} dispatched obligation(s) held.{coverageNote}",
            Duration = sw.Elapsed
        };
    }

    private MetricScore Warn(Stopwatch sw, string reason) => new()
    {
        MetricKey = Key,
        Score = 0.0,
        Verdict = Verdict.Warn,
        Reasoning = reason,
        Duration = sw.Elapsed
    };
}
