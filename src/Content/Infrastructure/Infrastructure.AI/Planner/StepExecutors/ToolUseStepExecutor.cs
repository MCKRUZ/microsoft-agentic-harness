using System.Diagnostics;
using System.Text.Json;
using Application.AI.Common.Extensions;
using Application.AI.Common.Interfaces.Attestation;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Planner;
using Application.AI.Common.Services.Governance;
using Application.AI.Common.Interfaces.Sandbox;
using Application.AI.Common.Services.Tools;
using Domain.AI.Escalation;
using Domain.AI.Governance;
using Domain.AI.Planner;
using Domain.AI.Sandbox;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Planner.StepExecutors;

/// <summary>
/// Executes tool steps by routing through the appropriate sandbox, verifying attestation,
/// and enforcing capability-based permissions with never-downgrade isolation.
/// </summary>
/// <remarks>
/// Before any sandbox resource is resolved, the tool call goes through
/// <see cref="IToolCallAdmissionPipeline"/> — the same chain, in the same order, that the live agent
/// tool path and the Execution API use. With no ambient capability envelope and every gate off, that
/// is a pure pass-through, so direct in-process <c>IPlanExecutor</c> callers behave exactly as before.
/// Under an enveloped run (armed by <c>PlanRunExecutor</c>) the chain enforces the per-caller grant
/// fail-closed: out-of-envelope tools, autonomy-ceiling violations, and identity-less calls all deny
/// before execution.
/// </remarks>
public sealed class ToolUseStepExecutor : IPlanStepExecutor
{
    private const string ReportedBy = "plan-executor";

    private readonly ICapabilityEnforcer _capabilityEnforcer;
    // Required, not optional-with-a-null-default, and deliberately so. An omitted admission chain is
    // indistinguishable at runtime from a host whose gates are all off, so a default would let a
    // composition that forgot to wire it run silently unguarded — the exact defect this dependency
    // exists to close. Absent registration should fail at resolution, loudly.
    private readonly IToolCallAdmissionPipeline _admissionPipeline;
    private readonly IServiceProvider _serviceProvider;
    private readonly IAttestationService _attestationService;
    private readonly IPlanProgressNotifier _notifier;
    private readonly PlanExecutionContext _executionContext;
    private readonly ILogger<ToolUseStepExecutor> _logger;
    // Shared bounded-key-set-gated lookup (#387) — the same one ToolPermissionProfileResolver,
    // ToolRiskClassifier, and ToolCapabilityResolver already read a tool's own declaration from.
    // Needed here for ITool.ResourceParametersByOperation (#418/#587): a name outside the bounded
    // first-party set (MCP/bundle-owned) resolves to null, same as everywhere else this lookup is used.
    private readonly FirstPartyToolLookup _firstPartyToolLookup;

    public ToolUseStepExecutor(
        ICapabilityEnforcer capabilityEnforcer,
        IToolCallAdmissionPipeline admissionPipeline,
        IServiceProvider serviceProvider,
        IAttestationService attestationService,
        IPlanProgressNotifier notifier,
        PlanExecutionContext executionContext,
        ILogger<ToolUseStepExecutor> logger,
        FirstPartyToolLookup firstPartyToolLookup)
    {
        _capabilityEnforcer = capabilityEnforcer;
        _admissionPipeline = admissionPipeline;
        _serviceProvider = serviceProvider;
        _attestationService = attestationService;
        _notifier = notifier;
        _executionContext = executionContext;
        _logger = logger;
        _firstPartyToolLookup = firstPartyToolLookup;
    }

    public async Task<StepExecutionResult> ExecuteAsync(
        PlanStep step,
        IReadOnlyDictionary<PlanStepId, string> upstreamOutputs,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        if (step.Configuration is not ToolUseConfig config)
        {
            return new StepExecutionResult
            {
                Status = StepExecutionStatus.Failed,
                Duration = TimeSpan.Zero,
                ErrorMessage = $"Step '{step.Name}' has invalid configuration type for ToolUse executor."
            };
        }

        // Built before authorization, not after, because these are what the tool will actually receive
        // and therefore what every argument-sensitive check needs to see: the approver reading the
        // request, an argument-conditioned policy rule, and the host's own observers. Declared
        // parameters alone would hide anything an upstream step fed into this one.
        var arguments = BuildToolArguments(config, upstreamOutputs);

        var (admission, refusal) = await AdmitToolAsync(config.ToolName, step.Name, arguments, sw, ct);
        if (refusal is not null)
            return refusal;

        var profile = await _capabilityEnforcer.ResolveProfileAsync(config.ToolName, ct);
        var isolationLevel = DetermineIsolation(config, profile, step);
        profile = profile.WithMinimumIsolationAtLeast(isolationLevel);

        var (sandboxResult, sandboxFailure) = await RunSandboxAsync(
            config, step, profile, isolationLevel, arguments, admission, sw, ct);
        if (sandboxFailure is not null)
            return sandboxFailure;

        var attestationFailure = await VerifyAttestationAsync(sandboxResult!, admission, config, step, sw, ct);
        if (attestationFailure is not null)
            return attestationFailure;

        sw.Stop();

        await _notifier.NotifySandboxStatusAsync(
            _executionContext.CurrentPlanId ?? new PlanId(Guid.Empty), step.Id, config.ToolName, isolationLevel,
            sandboxResult!.ResourceUsage ?? new ResourceUsage(),
            sandboxResult.Attestation?.Signature, ct);

        return sandboxResult.Success
            ? await HandleSuccessAsync(admission, config, sandboxResult, sw, ct)
            : await HandleFailureAsync(admission, config, step, sandboxResult, sw);
    }

    /// <summary>
    /// Runs the tool in its sandbox. Returns the result, or — on a thrown fault — a failed step
    /// result and no sandbox result. A cancellation is neither: it rethrows after reporting
    /// <see cref="EscalationExecutionStatus.NeverExecuted"/>, so it is not shaped as either tuple case.
    /// </summary>
    private async Task<(SandboxExecutionResult? Result, StepExecutionResult? Failure)> RunSandboxAsync(
        ToolUseConfig config,
        PlanStep step,
        ToolPermissionProfile profile,
        SandboxIsolationLevel isolationLevel,
        IReadOnlyDictionary<string, object?> arguments,
        ToolCallAdmission admission,
        Stopwatch sw,
        CancellationToken ct)
    {
        var request = new SandboxExecutionRequest
        {
            ToolName = config.ToolName,
            Input = JsonSerializer.Serialize(arguments),
            Limits = new ResourceLimits(),
            PermissionProfile = profile,
            Timeout = step.Timeout
        };

        var executor = _serviceProvider.GetRequiredKeyedService<ISandboxExecutor>(isolationLevel);
        try
        {
            // ExecuteNonNullAsync guards a custom executor (a template extensibility seam) violating
            // its non-nullable contract, found on this call site by /code-review on the #425 fix:
            // without it a null flowed past the sandboxFailure-null check below and into
            // VerifyAttestationAsync's forced `sandboxResult!` unwrap — an unhandled
            // NullReferenceException on the main plan-execution path. A thrown guard instead routes
            // into this method's own catch below, which already reports
            // EscalationExecutionStatus.Failed with the stable PlanStepErrors.SandboxFailed code — the
            // same graceful-degradation path every other sandbox fault here takes.
            return (await executor.ExecuteNonNullAsync(request, ct), null);
        }
        catch (OperationCanceledException)
        {
            // The one place #325 execution reporting needs a third outcome, not two. A plan run is
            // checkpointed and resumable, so "cancelled mid-call" is not the same claim as "failed" —
            // the step may still succeed when the plan resumes and re-admits it, and telling the
            // approver it failed would be a false report the resume then contradicts. Reported before
            // rethrowing so ExecuteStepAsync's own Cancelled/Failed step-status decision is untouched.
            await ReportExecutionAsync(
                admission, EscalationExecutionStatus.NeverExecuted, failureReason: null, config.ToolName,
                notExecutedReason: EscalationNotExecutedReason.RunCancelled);
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sw.Stop();
            // Full detail stays in the structured log; only a stable code is persisted onto the step,
            // because step error state is returned to callers and sandbox exceptions carry host paths,
            // container ids, and mount configuration.
            _logger.LogError(ex, "Sandbox execution threw for tool {Tool} in step {Step}", config.ToolName, step.Name);
            await ReportExecutionAsync(
                admission, EscalationExecutionStatus.Failed, PlanStepErrors.SandboxFailed, config.ToolName);
            return (null, new StepExecutionResult
            {
                Status = StepExecutionStatus.Failed,
                ErrorMessage = PlanStepErrors.SandboxFailed,
                Duration = sw.Elapsed
            });
        }
    }

    /// <summary>
    /// Verifies the sandbox result's attestation, when it carries one. Returns null when verification
    /// passed (or nothing needed verifying); otherwise the failed step result to return.
    /// </summary>
    private async Task<StepExecutionResult?> VerifyAttestationAsync(
        SandboxExecutionResult sandboxResult,
        ToolCallAdmission admission,
        ToolUseConfig config,
        PlanStep step,
        Stopwatch sw,
        CancellationToken ct)
    {
        if (sandboxResult.Attestation is null)
            return null;

        // When the attestation carries an output hash, verify BOUND to the actual returned output —
        // signature-only verification cannot detect a result whose Output was tampered after signing.
        // Legacy/output-less attestations (timeouts, spawn refusals) have nothing to bind and fall
        // back to signature verification.
        var verified = sandboxResult.Attestation.OutputHash is not null
            ? await _attestationService.VerifyBoundAsync(
                sandboxResult.Attestation, sandboxResult.Output ?? string.Empty, ct)
            : await _attestationService.VerifyAsync(sandboxResult.Attestation, ct);
        if (verified)
            return null;

        sw.Stop();
        _logger.LogWarning("Attestation verification failed for tool {Tool} in step {Step}",
            config.ToolName, step.Name);
        await ReportExecutionAsync(
            admission, EscalationExecutionStatus.Failed,
            "attestation verification failed: possible tampering detected", config.ToolName);
        return new StepExecutionResult
        {
            Status = StepExecutionStatus.Failed,
            ErrorMessage = "Attestation verification failed: possible tampering detected.",
            Duration = sw.Elapsed,
            Attestation = sandboxResult.Attestation
        };
    }

    /// <summary>Shapes a successful sandbox result into the step's completed (or policy-denied) outcome.</summary>
    private async Task<StepExecutionResult> HandleSuccessAsync(
        ToolCallAdmission admission, ToolUseConfig config, SandboxExecutionResult sandboxResult, Stopwatch sw,
        CancellationToken ct)
    {
        // Admission is not finished when the tool returns: a classified asset can be allowed through
        // and have its output scrubbed instead of being refused outright. Skipping this would leave
        // the gate's audit line and metric asserting a redaction that never happened, while the raw
        // content went back to the caller — a worse failure than not classifying at all, because it
        // reports itself as safe. #479: this call now sanitizes unconditionally on both the redact and
        // plain-allow branches — the separate unconditional Sanitize() call this method used to make
        // immediately afterward is gone, not just moved; making it again here would scrub twice.
        // Truncation discarded here, unlike in the Execution API (#532). This is the plan path's only
        // cut — there is no second, wider ceiling whose own measurement would contradict it — and a
        // step result has no truncation field to publish it on. The marker the cut leaves in the text
        // is what a later step or a reader sees. If StepExecutionResult ever gains such a field, this
        // is the value it takes.
        var textPolicy = await _admissionPipeline
            .TryApplyTextOutputPolicyAsync(admission, config.ToolName, sandboxResult.Output, ct)
            .ConfigureAwait(false);
        if (!textPolicy.Success)
        {
            await ReportExecutionAsync(
                admission, EscalationExecutionStatus.Failed, GovernanceDenials.NotPermitted(config.ToolName),
                config.ToolName);
            return new StepExecutionResult
            {
                Status = StepExecutionStatus.Failed,
                ErrorMessage = GovernanceDenials.NotPermitted(config.ToolName),
                Duration = sw.Elapsed,
                IsPolicyDenial = true,
                Attestation = sandboxResult.Attestation
            };
        }

        await ReportExecutionAsync(admission, EscalationExecutionStatus.Succeeded, failureReason: null, config.ToolName);

        // #490: StepExecutionResult.Output is documented as "null if the step produced no output" —
        // textPolicy.Text is now non-nullable (string.Empty on NothingToAdmit), so mapping it straight
        // through would silently turn "no output" into an empty-string output, a behavior change to a
        // persisted plan step this fix has no business making. NothingToAdmit maps back to null,
        // preserving Output's existing contract; every other reachable outcome here is Admitted
        // (Withheld already returned above).
        return new StepExecutionResult
        {
            Status = StepExecutionStatus.Completed,
            Output = textPolicy.Outcome == TextOutputPolicyOutcome.NothingToAdmit ? null : textPolicy.Text,
            Duration = sw.Elapsed,
            Attestation = sandboxResult.Attestation
        };
    }

    /// <summary>Shapes a failed sandbox result into the step's failed outcome.</summary>
    private async Task<StepExecutionResult> HandleFailureAsync(
        ToolCallAdmission admission, ToolUseConfig config, PlanStep step, SandboxExecutionResult sandboxResult, Stopwatch sw)
    {
        // Same treatment as the throw path in RunSandboxAsync: the sandbox's failure text is raw
        // process stderr, a raw exception message, or raw container logs, and step error state is
        // persisted and returned to callers. Log it in full, persist only the stable code.
        _logger.LogWarning(
            "Tool {Tool} in step {Step} failed in the sandbox: {SandboxError}",
            config.ToolName, step.Name, sandboxResult.ErrorMessage ?? "(no detail reported)");

        await ReportExecutionAsync(admission, EscalationExecutionStatus.Failed, PlanStepErrors.ToolFailed, config.ToolName);

        return new StepExecutionResult
        {
            Status = StepExecutionStatus.Failed,
            ErrorMessage = PlanStepErrors.ToolFailed,
            Duration = sw.Elapsed,
            Attestation = sandboxResult.Attestation
        };
    }

    /// <summary>
    /// Closes the approval loop for this step's call, when it was one a human approved. A no-op for
    /// every other call — see <see cref="IToolCallAdmissionPipeline.ReportExecutionAsync"/>.
    /// </summary>
    /// <remarks>
    /// Reported on <see cref="CancellationToken.None"/>, not the step's own token: by the time this
    /// runs the outcome is already known, and a plan-level cancellation racing the report must not be
    /// the reason an approver never learns whether their approved action actually ran.
    /// </remarks>
    private ValueTask ReportExecutionAsync(
        ToolCallAdmission admission,
        EscalationExecutionStatus status,
        string? failureReason,
        string toolName,
        EscalationNotExecutedReason? notExecutedReason = null) =>
        _admissionPipeline.ReportExecutionAsync(
            admission, new ToolExecutionReport(status, failureReason, notExecutedReason, ToolName: toolName),
            ReportedBy, CancellationToken.None);

    /// <summary>
    /// Runs the tool call through the admission chain and, when refused, produces the failed step
    /// result. Returns null when the call is allowed. The refusal message is already scrubbed for
    /// model/caller consumption (rule ids and policy internals stay in the governance trace and
    /// structured log), so it is safe to surface as the step error.
    /// </summary>
    /// <remarks>
    /// A plan step is a tool call like any other, and runs the same chain the agent's conversational
    /// path runs. Anything less would let a plan reach a tool the harness refuses in a chat turn — the
    /// agent could bypass a control simply by emitting a plan step instead of calling the tool
    /// directly, which is exactly the gap this chain exists to close.
    /// <para>
    /// #587: <see cref="ToolCallAdmissionRequest.ResourceRequest"/> must be populated here the same
    /// way <c>GovernedAIFunction</c> and <c>DirectToolInvoker</c> already populate it (#418) — a plan
    /// step is a third, independent admission path, and <c>CapabilityEnforcer</c>'s own fail-closed
    /// design refuses any tool with path/host scoping configured when this stays unset, even for a
    /// call whose paths/hosts were genuinely in bounds.
    /// </para>
    /// </remarks>
    private async Task<(ToolCallAdmission Admission, StepExecutionResult? Refusal)> AdmitToolAsync(
        string toolName,
        string stepName,
        IReadOnlyDictionary<string, object?> arguments,
        Stopwatch sw,
        CancellationToken ct)
    {
        var admission = await _admissionPipeline
            .AdmitAsync(new ToolCallAdmissionRequest(toolName, arguments, ResourceRequest: ExtractResourceRequest(toolName, arguments)), ct);
        if (admission.IsAllowed)
            return (admission, null);

        sw.Stop();
        _logger.LogWarning(
            "Tool {Tool} refused by the admission chain in step {Step}", toolName, stepName);
        return (admission, new StepExecutionResult
        {
            Status = StepExecutionStatus.Failed,
            ErrorMessage = admission.DeniedMessage ?? GovernanceDenials.NotPermitted(toolName),
            Duration = sw.Elapsed,
            IsPolicyDenial = true
        });
    }

    /// <summary>
    /// Extracts the requested paths/hosts for one plan step's tool call — the same
    /// <see cref="ResourceParameterExtractor.Extract"/> both other admission entry points call (#418),
    /// applied to a plan step's own shape.
    /// </summary>
    /// <remarks>
    /// <see cref="Domain.AI.Planner.ToolUseConfig"/> carries no separate operation field — a plan
    /// author (or the LLM planner) puts the operation under the well-known <c>"operation"</c> key
    /// inside the flat argument set, the same wire-format key <see cref="AIToolConverter"/> and
    /// <see cref="GovernedAIFunction"/> already use for exactly this concept.
    /// <para>
    /// A plan step's own flat values are NOT reliably plain CLR values, unlike the doc comment this
    /// replaced claimed (correctness/security review on #587): <c>LlmPlanOutputMapper</c> does convert
    /// straight to scalars, but a step submitted through the workflow HTTP surface
    /// (<c>WorkflowDefinitionMapper</c>/<c>ToolUseStepConfiguration</c>) round-trips through
    /// System.Text.Json with no <c>object</c>-typed converter registered
    /// (<c>ExecutionApiServiceCollectionExtensions</c> only adds <c>JsonStringEnumConverter</c>), so its
    /// <c>object?</c>-typed dictionary values bind as <see cref="JsonElement"/>, not <see cref="string"/>.
    /// Left unhandled, that producer's calls would silently never populate a resource request at all —
    /// fail-closed (no bypass), but #587's own goal of enforcing identically across all three admission
    /// paths would quietly not hold for it. <see cref="NormalizeScalar"/> unwraps a string-valued
    /// <see cref="JsonElement"/> the same way <see cref="GovernedAIFunction"/> already does for its own
    /// wire shape, so both plan-authoring producers reach <see cref="ResourceParameterExtractor.Extract"/>
    /// on equal footing.
    /// </para>
    /// <para>
    /// Case-insensitive by construction (correctness review on #587), matching both other admission
    /// paths: <c>ToolParameters.Flatten</c> (the agent-turn path) hands <see cref="ResourceParameterExtractor.Extract"/>
    /// an <see cref="StringComparer.OrdinalIgnoreCase"/> dictionary, and a step naming a declared
    /// parameter with different casing (e.g. <c>"Path"</c> against a declared <c>"path"</c>) must not
    /// silently miss the match — that would resolve to <see cref="Domain.AI.Sandbox.ToolCallResourceRequest.Empty"/>,
    /// which <c>CapabilityEnforcer</c> treats as "nothing to check" and allows.
    /// </para>
    /// <para>
    /// Built with a last-write-wins loop, not <see cref="Enumerable.ToDictionary{TSource,TKey,TElement}(IEnumerable{TSource},Func{TSource,TKey},Func{TSource,TElement})"/>
    /// (grader/correctness review on #587): <paramref name="arguments"/> is itself ordinal —
    /// <c>BuildToolArguments</c> merges an upstream step's JSON output into the step's own declared
    /// parameters via <c>TryAdd</c> on a case-sensitive dictionary — so it can legitimately hold two
    /// keys that are case-variants of each other (e.g. a declared <c>"path"</c> alongside an
    /// upstream-produced <c>"Path"</c>). Re-keying that into an <see cref="StringComparer.OrdinalIgnoreCase"/>
    /// dictionary via <c>ToDictionary</c> would throw <see cref="ArgumentException"/> on the second,
    /// colliding key instead of resolving it — an admission-time crash that reaches
    /// <c>PlanExecutor</c>'s broad exception handler uncaught, which is a materially worse failure mode
    /// than the clean, traced refusal every other path in this admission chain produces.
    /// </para>
    /// </remarks>
    private Domain.AI.Sandbox.ToolCallResourceRequest? ExtractResourceRequest(
        string toolName, IReadOnlyDictionary<string, object?> arguments)
    {
        // Resolved first so a tool with no resource-parameter declaration (or one outside the bounded
        // first-party set) skips the dictionary copy below entirely — Extract would return null anyway.
        var tool = _firstPartyToolLookup.Resolve(toolName);
        if (tool?.ResourceParametersByOperation is not { Count: > 0 } declared)
            return null;

        var normalizedArguments = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in arguments)
            normalizedArguments[key] = NormalizeScalar(value);

        // "operation" duplicates AIToolConverter.OperationArgumentName's value — that constant is
        // internal to a different assembly (Application.AI.Common) and not visible here. Keep in sync.
        // Read from the normalized dictionary, not the raw arguments, so a differently-cased key
        // ("Operation") matches the same way every declared resource-parameter name already does.
        var operation = normalizedArguments.TryGetValue("operation", out var operationValue)
            ? operationValue as string
            : null;

        return ResourceParameterExtractor.Extract(operation, normalizedArguments, declared);
    }

    /// <summary>
    /// Unwraps a string-valued <see cref="JsonElement"/> to the plain <see cref="string"/>
    /// <see cref="ResourceParameterExtractor.Extract"/> expects; every other shape (already a CLR
    /// scalar, or a non-string <see cref="JsonElement"/> that could never be a path/host anyway) passes
    /// through unchanged.
    /// </summary>
    private static object? NormalizeScalar(object? value) => value switch
    {
        JsonElement { ValueKind: JsonValueKind.String } je => je.GetString(),
        _ => value
    };

    private static SandboxIsolationLevel DetermineIsolation(
        ToolUseConfig config,
        ToolPermissionProfile profile,
        PlanStep step)
    {
        var level = profile.MinimumIsolation;

        if (config.IsolationLevelOverride.HasValue)
            level = level.AtLeast(config.IsolationLevelOverride.Value);

        if (step.RequiredAutonomyLevel is AutonomyLevel.Supervised or AutonomyLevel.Restricted)
            level = level.AtLeast(SandboxIsolationLevel.Container);

        // Floor None to Process: no ISandboxExecutor is keyed for None (only Process and
        // Container are registered). A tool that doesn't override ITool.MinimumIsolation resolves
        // to a profile with MinimumIsolation = None, which would otherwise throw
        // InvalidOperationException at keyed-service resolution. Process is the default
        // subprocess executor and the safe minimum for "direct-execution" tools.
        return level.AtLeast(SandboxIsolationLevel.Process);
    }

    /// <summary>
    /// Merges the step's declared parameters with any JSON object fields produced by upstream steps
    /// into the effective argument set the tool will be invoked with.
    /// </summary>
    private static Dictionary<string, object?> BuildToolArguments(
        ToolUseConfig config,
        IReadOnlyDictionary<PlanStepId, string> upstreamOutputs)
    {
        var merged = new Dictionary<string, object?>(config.InputParameters);

        foreach (var (_, output) in upstreamOutputs)
        {
            if (string.IsNullOrEmpty(output)) continue;
            try
            {
                using var doc = JsonDocument.Parse(output);
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    merged.TryAdd(prop.Name, prop.Value.GetRawText());
                }
            }
            catch (JsonException) { }
        }

        return merged;
    }
}
