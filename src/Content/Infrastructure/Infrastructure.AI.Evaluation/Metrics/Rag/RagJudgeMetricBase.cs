using System.Diagnostics;
using Application.AI.Common.Evaluation.Interfaces;
using Application.AI.Common.Evaluation.Models;
using Application.AI.Common.Evaluation.Outcomes;
using Application.AI.Common.Prompts.Exceptions;
using Application.AI.Common.Prompts.Interfaces;
using Domain.AI.Evaluation;
using Domain.AI.Prompts;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Evaluation.Metrics.Rag;

/// <summary>
/// Shared base for the RAG metric pack (faithfulness, context precision/recall,
/// answer relevance/correctness). Centralizes prompt resolution from the versioned
/// <see cref="IPromptRegistry"/>, per-case attribution via
/// <see cref="IPromptUsageRecorder"/>, required-field validation, judge dispatch
/// through the structured <see cref="ILlmJudge"/>, threshold comparison, and
/// soft-fail-to-Warn for all expected failure modes.
/// </summary>
/// <remarks>
/// <para>
/// The nonce + HtmlEncode injection mitigations live inside <see cref="ILlmJudge"/>
/// (the structured-request boundary) so subclasses cannot accidentally bypass them.
/// Subclasses supply only metadata: <see cref="Key"/>, optionally <see cref="PromptName"/>
/// when the default convention doesn't apply, <see cref="RequiredFields"/>, and
/// <see cref="BuildVariables"/>.
/// </para>
/// <para>
/// <b>Single-flight descriptor cache.</b> The first ScoreAsync resolves the
/// descriptor via a <c>Lazy&lt;Task&lt;PromptDescriptor&gt;&gt;</c>; subsequent and
/// concurrent callers await the same in-flight task. A faulted Lazy is evicted
/// (via <see cref="Interlocked.CompareExchange{T}"/>) so the next caller retries
/// instead of replaying a stale failure. Same pattern as
/// <c>DefaultJudgeChatClientProvider</c>.
/// </para>
/// <para>
/// <b>Exception contract.</b> Relies on the <see cref="IPromptRegistry"/> contract:
/// the only soft-fail-to-Warn surface is <see cref="KeyNotFoundException"/> (the
/// prompt name is unknown) + <see cref="PromptRegistryUnavailableException"/> (the
/// backend cannot serve right now). Any other exception escaping the registry is
/// a contract defect and propagates so the suite fails loudly rather than masking.
/// </para>
/// <para>
/// <b>Recorder contract.</b> Trusts the <see cref="IPromptUsageRecorder"/> "Never
/// throws" contract — no defensive try/catch around <see cref="IPromptUsageRecorder.RecordAsync"/>.
/// Recorder implementations are responsible for their own exception safety.
/// </para>
/// </remarks>
public abstract class RagJudgeMetricBase : IEvalMetric
{
    private readonly ILlmJudge _judge;
    private readonly IPromptRegistry _promptRegistry;
    private readonly IPromptUsageRecorder _usageRecorder;
    private readonly ILogger _logger;

    // Single-flight: all concurrent first-callers await the same Task. Replaced on
    // faulted observation so the next caller triggers a fresh resolution.
    private Lazy<Task<PromptDescriptor>> _descriptor;

    private const string SystemPromptCore =
        "You are an evaluation judge. Score the supplied data per the rubric in the user prompt. " +
        "Respond ONLY with a single JSON object: {\"score\": <0.0-1.0>, \"reasoning\": \"<one or two sentences>\"}. " +
        "Do not include markdown fences, prose, or any text outside the JSON object.";

    /// <summary>Required fields a case must supply for this metric to score.</summary>
    [Flags]
    protected enum RagInputs
    {
        /// <summary>No special requirement.</summary>
        None = 0,
        /// <summary>The case's <c>Input</c> must be non-empty.</summary>
        Input = 1 << 0,
        /// <summary>The case's <c>ExpectedOutput</c> must be non-empty.</summary>
        ExpectedOutput = 1 << 1,
        /// <summary>The case's <c>RetrievedContext</c> must be non-empty.</summary>
        RetrievedContext = 1 << 2,
        /// <summary>The agent invocation's <c>Output</c> must be non-empty.</summary>
        Output = 1 << 3,
    }

    /// <summary>Initializes a new instance.</summary>
    /// <param name="judge">Shared judge call service.</param>
    /// <param name="promptRegistry">Versioned prompt registry; resolves the metric's judge template by name.</param>
    /// <param name="usageRecorder">Stamps OTel / persists which prompt version each case used (for trace replay).</param>
    /// <param name="logger">Logger for missing-input and resolution diagnostics.</param>
    protected RagJudgeMetricBase(
        ILlmJudge judge,
        IPromptRegistry promptRegistry,
        IPromptUsageRecorder usageRecorder,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(judge);
        ArgumentNullException.ThrowIfNull(promptRegistry);
        ArgumentNullException.ThrowIfNull(usageRecorder);
        ArgumentNullException.ThrowIfNull(logger);

        _judge = judge;
        _promptRegistry = promptRegistry;
        _usageRecorder = usageRecorder;
        _logger = logger;
        _descriptor = CreateDescriptorLazy();
    }

    /// <inheritdoc />
    public abstract string Key { get; }

    /// <summary>
    /// Registry name of the metric's judge prompt. Defaults to the convention
    /// <c>{Key.Replace('_', '-').ToLowerInvariant()}-judge</c> (e.g.
    /// <c>"faithfulness"</c> → <c>"faithfulness-judge"</c>). Override only when the
    /// metric needs a non-conventional registry name (version pinning, A/B variant).
    /// </summary>
    /// <remarks>
    /// <see cref="Key"/> must be lowercase snake_case for the convention to produce
    /// a valid registry name. Non-conforming keys should override <see cref="PromptName"/>
    /// explicitly.
    /// </remarks>
    protected virtual string PromptName => $"{Key.Replace('_', '-').ToLowerInvariant()}-judge";

    /// <summary>Fields the metric needs populated; missing fields → <see cref="Verdict.Warn"/> without invoking the judge.</summary>
    protected abstract RagInputs RequiredFields { get; }

    /// <summary>Builds the variable dictionary substituted into the template.</summary>
    protected abstract IReadOnlyDictionary<string, string?> BuildVariables(EvalCase @case, AgentInvocationResult output);

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

        var missingField = FindMissingRequired(@case, output);
        if (missingField is not null)
        {
            return Warn(sw, $"{Key} requires {missingField} to be populated for the case.");
        }

        PromptDescriptor descriptor;
        try
        {
            descriptor = await ResolveDescriptorAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (KeyNotFoundException ex)
        {
            _logger.LogWarning(ex, "Prompt '{Prompt}' missing in registry for metric {Metric}.", PromptName, Key);
            return Warn(sw, $"Prompt template '{PromptName}' not found for metric {Key}.");
        }
        catch (PromptRegistryUnavailableException ex)
        {
            _logger.LogWarning(ex, "Prompt registry unavailable for '{Prompt}' on metric {Metric}.", PromptName, Key);
            return Warn(sw, $"Failed to resolve prompt template '{PromptName}' for metric {Key}: {ex.Message}");
        }

        // Per-case attribution. Trust the IPromptUsageRecorder contract ("Never throws") —
        // any failure here is a defect in the recorder impl, not a runtime condition.
        await _usageRecorder.RecordAsync(
            descriptor,
            new PromptUsageContext { CaseId = @case.Id, MetricKey = Key },
            cancellationToken).ConfigureAwait(false);

        LlmJudgeResult result;
        try
        {
            result = await _judge.JudgeAsync(
                new LlmJudgeRequest
                {
                    SystemPromptCore = SystemPromptCore,
                    UserPromptTemplate = descriptor.Body,
                    Variables = BuildVariables(@case, output),
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
            // Belt-and-suspenders: the judge contract is soft-fail-to-Warn for expected
            // failures, but we catch any escaped exception (e.g. template bug, unexpected
            // judge impl issue) so a single bad case can't abort the entire eval suite.
            _logger.LogWarning(ex, "Judge invocation escaped contract for metric {Metric}.", Key);
            return Warn(sw, $"Judge invocation error: {ex.Message}");
        }

        sw.Stop();

        return JudgeMetricScoreMapper.ToMetricScore(Key, result, spec.Threshold, sw.Elapsed);
    }

    private Lazy<Task<PromptDescriptor>> CreateDescriptorLazy()
        => new(() => _promptRegistry.GetLatestAsync(PromptName, CancellationToken.None),
            LazyThreadSafetyMode.ExecutionAndPublication);

    private async Task<PromptDescriptor> ResolveDescriptorAsync(CancellationToken cancellationToken)
    {
        // Snapshot the field so an EvictFaulted race that swaps the Lazy doesn't
        // confuse our awaiting/eviction logic.
        var lazy = _descriptor;
        try
        {
            // WaitAsync surfaces caller cancellation while leaving the underlying
            // task to be observed by the next caller.
            return await lazy.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Resolution failed (typically KeyNotFoundException or PromptRegistryUnavailable
            // surfaced through the registry contract). Without eviction, the faulted Lazy
            // would be cached forever and every subsequent call would replay the same
            // exception until process restart. CompareExchange only replaces the snapshot
            // we observed — concurrent successful resolutions are preserved.
            var fresh = CreateDescriptorLazy();
            Interlocked.CompareExchange(ref _descriptor, fresh, lazy);
            throw;
        }
    }

    private string? FindMissingRequired(EvalCase @case, AgentInvocationResult output)
    {
        if (RequiredFields.HasFlag(RagInputs.Input) && string.IsNullOrWhiteSpace(@case.Input))
            return "case.Input";
        if (RequiredFields.HasFlag(RagInputs.ExpectedOutput) && string.IsNullOrWhiteSpace(@case.ExpectedOutput))
            return "case.ExpectedOutput";
        if (RequiredFields.HasFlag(RagInputs.RetrievedContext) && string.IsNullOrWhiteSpace(@case.RetrievedContext))
            return "case.RetrievedContext";
        if (RequiredFields.HasFlag(RagInputs.Output) && string.IsNullOrWhiteSpace(output.Output))
            return "output.Output";
        return null;
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
