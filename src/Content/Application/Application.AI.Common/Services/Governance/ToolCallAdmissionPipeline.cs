using System.Collections.ObjectModel;
using System.Text.Json;
using Application.AI.Common.Interfaces.Governance;
using Domain.AI.Governance;
using Microsoft.Extensions.Logging;

namespace Application.AI.Common.Services.Governance;

/// <summary>
/// Default <see cref="IToolCallAdmissionPipeline"/>: runs the four admission stages in the one order
/// that is safe, and owns everything that follows from that ordering.
/// </summary>
/// <remarks>
/// <para>
/// The ordering rationale lives on <see cref="IToolCallAdmissionPipeline"/> and is not repeated here.
/// What this type adds beyond sequencing:
/// </para>
/// <list type="bullet">
/// <item><description>
/// A caller's absent arguments become an empty dictionary before any stage that requires one, so a
/// consumer-authored rule can read arguments without a null check. "The caller had none" and "the call
/// had none" are the same thing to a rule.
/// </description></item>
/// <item><description>
/// The loop guard's call signature is computed here and computed lazily, so a call the earlier stages
/// refuse never pays for serialising its arguments.
/// </description></item>
/// <item><description>
/// A refusal always carries text. Every stage's own refusal factory already requires a message, so the
/// fallback is unreachable defence — but it resolves to the one canonical denial rather than to text
/// naming the stage, because a caller must not be able to tell from the message which gate refused.
/// </description></item>
/// </list>
/// </remarks>
public sealed class ToolCallAdmissionPipeline : IToolCallAdmissionPipeline
{
    // Unit-separator (U+001F) cannot appear in a JSON-serialised value, so distinct argument sets
    // cannot collide into the same joined signature. Built from a char code to keep the source ASCII.
    private static readonly string ArgPairSeparator = ((char)0x1F).ToString();

    // Shared across every call and handed to consumer-authored code, so it is a ReadOnlyDictionary
    // rather than a bare Dictionary — a rule could otherwise downcast it and mutate it for everyone.
    private static readonly IReadOnlyDictionary<string, object?> EmptyArguments =
        ReadOnlyDictionary<string, object?>.Empty;

    private readonly IToolInvocationGovernor _governor;
    private readonly IToolClassificationGate _classificationGate;
    private readonly IToolCallObserverChain _observers;
    private readonly IProgressEvaluator _progressEvaluator;
    private readonly ILogger<ToolCallAdmissionPipeline> _logger;

    /// <summary>Initializes a new instance of the <see cref="ToolCallAdmissionPipeline"/> class.</summary>
    /// <param name="governor">Stage 1 — permission, capability, envelope and declarative policy.</param>
    /// <param name="classificationGate">
    /// Stage 2 — data sensitivity. Required rather than optional: it is registered unconditionally and
    /// reports its own off state internally, so an absent one would be indistinguishable at runtime from
    /// a host that turned classification off, and only one of those is safe.
    /// </param>
    /// <param name="observers">
    /// Stage 3 — the host's own rules. Required for the same reason: an absent chain and a chain with
    /// nothing in it are indistinguishable at runtime.
    /// </param>
    /// <param name="progressEvaluator">Stage 4 — the loop guard.</param>
    /// <param name="logger">Records a redaction that could not be applied.</param>
    public ToolCallAdmissionPipeline(
        IToolInvocationGovernor governor,
        IToolClassificationGate classificationGate,
        IToolCallObserverChain observers,
        IProgressEvaluator progressEvaluator,
        ILogger<ToolCallAdmissionPipeline> logger)
    {
        ArgumentNullException.ThrowIfNull(governor);
        ArgumentNullException.ThrowIfNull(classificationGate);
        ArgumentNullException.ThrowIfNull(observers);
        ArgumentNullException.ThrowIfNull(progressEvaluator);
        ArgumentNullException.ThrowIfNull(logger);

        _governor = governor;
        _classificationGate = classificationGate;
        _observers = observers;
        _progressEvaluator = progressEvaluator;
        _logger = logger;
    }

    /// <inheritdoc />
    public async ValueTask<ToolCallAdmission> AdmitAsync(
        ToolCallAdmissionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var toolName = request.ToolName;
        var arguments = request.Arguments ?? EmptyArguments;

        // 1 — the built-in governor. Arguments are passed through as the caller supplied them,
        // null included: the governor distinguishes "no arguments were available" from "the call had
        // none", and narrows its argument-conditioned rules accordingly.
        var decision = await _governor
            .AuthorizeAsync(toolName, cancellationToken, request.Arguments)
            .ConfigureAwait(false);
        if (!decision.IsAllowed)
            return Refuse(decision.DeniedMessage, toolName);

        // 2 — data classification, for calls that have a data surface to classify. A block refuses the
        // call outright; a redact verdict lets it run and scrubs the output afterwards, which is why
        // the verdict survives past this stage.
        //
        // A request carrying NO arguments is a capability gate, not a tool call — "may this run call a
        // model at all", "may it retrieve at all" — and there is nothing for an asset resolver to
        // resolve. Running the gate anyway would not classify anything; it would resolve to Unknown
        // and hand the decision to the host's unknown-asset policy, which is a verdict about the
        // absence of information rather than about the call. A host that hardens that policy to Block
        // would then fail every LLM-call and retrieval step in every plan.
        //
        // This is a property of the REQUEST, not of the calling path, so it stays uniform: every
        // caller with arguments is classified and every caller without is not. A tool call always has
        // an argument dictionary even when it is empty — only the two plan capability gates pass null
        // — so no real tool call can slip through this.
        var classification = ClassificationVerdict.Allow();
        if (request.Arguments is not null)
        {
            classification = await _classificationGate
                .EvaluateAsync(toolName, arguments, cancellationToken)
                .ConfigureAwait(false);
            if (classification.Outcome == ClassificationGateOutcome.Block)
                return Refuse(classification.BlockedMessage, toolName);
        }

        // 3 — the host's own rules, last of the access gates.
        if (_observers.HasObservers)
        {
            var observed = await _observers
                .EvaluateAsync(toolName, arguments, cancellationToken)
                .ConfigureAwait(false);
            if (!observed.IsAllowed)
                return Refuse(observed.DeniedMessage, toolName);
        }

        // 4 — the loop guard, last of all, and only for callers that issue a sequence.
        if (request.CountsTowardLoopDetection)
        {
            var verdict = _progressEvaluator.Evaluate(
                toolName, () => ComputeArgumentsSignature(request.Arguments));
            if (verdict.ShouldHalt)
                return Refuse(verdict.HaltMessage, toolName);
        }

        return classification.Outcome == ClassificationGateOutcome.RedactOutput
            ? ToolCallAdmission.AllowWithOutputRedaction()
            : ToolCallAdmission.Allow();
    }

    /// <inheritdoc />
    public object? ApplyOutputPolicy(ToolCallAdmission admission, string toolName, object? result)
    {
        ArgumentNullException.ThrowIfNull(admission);

        return admission.RedactsOutput
            ? _classificationGate.RedactResult(toolName, result)
            : result;
    }

    /// <inheritdoc />
    public bool TryApplyTextOutputPolicy(
        ToolCallAdmission admission, string toolName, string? content, out string? result)
    {
        ArgumentNullException.ThrowIfNull(admission);

        if (!admission.RedactsOutput)
        {
            result = content;
            return true;
        }

        var redacted = _classificationGate.RedactResult(toolName, content);
        if (redacted is string text)
        {
            result = text;
            return true;
        }

        // Fail closed. The gate decided this asset must not be emitted as-is, so falling back to the
        // original is precisely the harmless-looking default that would defeat the control — which is
        // what `RedactResult(...) as string ?? content` would have done. The shipped gate always
        // answers with a string here, so this guards against a consumer-supplied one.
        _logger.LogWarning(
            "Classification gate returned a {ResultType} rather than a string when redacting output of "
            + "{ToolName}; the result is withheld rather than returned unredacted.",
            redacted?.GetType().Name ?? "null",
            toolName);

        result = null;
        return false;
    }

    /// <inheritdoc />
    public GovernanceTrace GetTrace()
    {
        var trace = _governor.GetTrace();
        var spinEscalations = _progressEvaluator.EscalationReasonCodes;
        if (spinEscalations is null or { Count: 0 })
            return trace;

        // Dedup case-insensitively to honour GovernanceTrace.EscalationReasonCodes' "distinct" contract
        // and stay aligned with GovernanceTrace.Merge's OrdinalIgnoreCase union.
        return trace with
        {
            EscalationReasonCodes =
                [.. trace.EscalationReasonCodes.Concat(spinEscalations).Distinct(StringComparer.OrdinalIgnoreCase)]
        };
    }

    /// <inheritdoc />
    public void Reset()
    {
        _governor.Reset();
        _progressEvaluator.Reset();
    }

    // Blank counts as absent, not just null: whitespace reaches a model as indistinguishable from an
    // empty result, which reads as the tool having run and returned nothing rather than as a refusal.
    private static ToolCallAdmission Refuse(string? stageMessage, string toolName) =>
        ToolCallAdmission.Deny(
            string.IsNullOrWhiteSpace(stageMessage) ? GovernanceDenials.NotPermitted(toolName) : stageMessage);

    /// <summary>
    /// Builds a stable, deterministic signature of the call arguments so the loop guard can recognise
    /// identical calls. Keys are ordered; each value is JSON-serialised, falling back to its type name
    /// if serialisation throws — the signature is always computable and never throws on the hot path.
    /// </summary>
    private static string? ComputeArgumentsSignature(IReadOnlyDictionary<string, object?>? arguments)
    {
        if (arguments is null || arguments.Count == 0)
            return string.Empty;

        var parts = new List<string>(arguments.Count);
        foreach (var kvp in arguments.OrderBy(a => a.Key, StringComparer.Ordinal))
        {
            string value;
            try
            {
                value = kvp.Value is null ? "null" : JsonSerializer.Serialize(kvp.Value);
            }
            catch
            {
                value = kvp.Value?.GetType().FullName ?? "null";
            }

            parts.Add(string.Concat(kvp.Key, "=", value));
        }

        return string.Join(ArgPairSeparator, parts);
    }
}
