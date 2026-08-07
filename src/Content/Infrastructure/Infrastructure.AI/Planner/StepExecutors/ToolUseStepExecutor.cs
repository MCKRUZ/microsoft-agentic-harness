using System.Diagnostics;
using System.Text.Json;
using Application.AI.Common.Interfaces.Attestation;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Planner;
using Application.AI.Common.Services.Governance;
using Application.AI.Common.Interfaces.Sandbox;
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
/// Before any sandbox resource is resolved, the tool call is authorized through
/// <see cref="IToolInvocationGovernor"/> — the same choke point the live agent tool path uses. With no
/// ambient capability envelope and per-invocation enforcement off, that authorization is a pure
/// pass-through, so direct in-process <c>IPlanExecutor</c> callers behave exactly as before. Under an
/// enveloped run (armed by <c>PlanRunExecutor</c>) the governor enforces the per-caller grant fail-closed:
/// out-of-envelope tools, autonomy-ceiling violations, and identity-less calls all deny before execution.
/// </remarks>
public sealed class ToolUseStepExecutor : IPlanStepExecutor
{
    private readonly ICapabilityEnforcer _capabilityEnforcer;
    private readonly IToolInvocationGovernor _toolInvocationGovernor;
    // Required, not optional-with-a-null-default, and deliberately so. An omitted observer chain is
    // indistinguishable at runtime from a host that registered no rules, so a default would let a
    // composition that forgot to wire the seam run silently unguarded — the exact defect this
    // dependency exists to close. Absent registration should fail at resolution, loudly.
    private readonly IToolCallObserverChain _observers;
    private readonly IServiceProvider _serviceProvider;
    private readonly IAttestationService _attestationService;
    private readonly ICompositeResponseSanitizer _responseSanitizer;
    private readonly IPlanProgressNotifier _notifier;
    private readonly PlanExecutionContext _executionContext;
    private readonly ILogger<ToolUseStepExecutor> _logger;

    public ToolUseStepExecutor(
        ICapabilityEnforcer capabilityEnforcer,
        IToolInvocationGovernor toolInvocationGovernor,
        IToolCallObserverChain observers,
        IServiceProvider serviceProvider,
        IAttestationService attestationService,
        ICompositeResponseSanitizer responseSanitizer,
        IPlanProgressNotifier notifier,
        PlanExecutionContext executionContext,
        ILogger<ToolUseStepExecutor> logger)
    {
        _capabilityEnforcer = capabilityEnforcer;
        _toolInvocationGovernor = toolInvocationGovernor;
        _observers = observers;
        _serviceProvider = serviceProvider;
        _attestationService = attestationService;
        _responseSanitizer = responseSanitizer;
        _notifier = notifier;
        _executionContext = executionContext;
        _logger = logger;
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

        var denial = await AuthorizeToolAsync(config.ToolName, step.Name, arguments, sw, ct);
        if (denial is not null)
            return denial;

        var profile = await _capabilityEnforcer.ResolveProfileAsync(config.ToolName, ct);
        var isolationLevel = DetermineIsolation(config, profile, step);

        var input = JsonSerializer.Serialize(arguments);
        var request = new SandboxExecutionRequest
        {
            ToolName = config.ToolName,
            Input = input,
            Limits = new ResourceLimits(),
            PermissionProfile = profile,
            Timeout = step.Timeout
        };

        var executor = _serviceProvider.GetRequiredKeyedService<ISandboxExecutor>(isolationLevel);
        SandboxExecutionResult sandboxResult;
        try
        {
            sandboxResult = await executor.ExecuteAsync(request, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sw.Stop();
            // Full detail stays in the structured log; only a stable code is persisted onto the step,
            // because step error state is returned to callers and sandbox exceptions carry host paths,
            // container ids, and mount configuration.
            _logger.LogError(ex, "Sandbox execution threw for tool {Tool} in step {Step}", config.ToolName, step.Name);
            return new StepExecutionResult
            {
                Status = StepExecutionStatus.Failed,
                ErrorMessage = PlanStepErrors.SandboxFailed,
                Duration = sw.Elapsed
            };
        }

        if (sandboxResult.Attestation is not null)
        {
            // When the attestation carries an output hash, verify BOUND to the actual
            // returned output — signature-only verification cannot detect a result whose
            // Output was tampered after signing. Legacy/output-less attestations (timeouts,
            // spawn refusals) have nothing to bind and fall back to signature verification.
            var verified = sandboxResult.Attestation.OutputHash is not null
                ? await _attestationService.VerifyBoundAsync(
                    sandboxResult.Attestation, sandboxResult.Output ?? string.Empty, ct)
                : await _attestationService.VerifyAsync(sandboxResult.Attestation, ct);
            if (!verified)
            {
                sw.Stop();
                _logger.LogWarning("Attestation verification failed for tool {Tool} in step {Step}",
                    config.ToolName, step.Name);
                return new StepExecutionResult
                {
                    Status = StepExecutionStatus.Failed,
                    ErrorMessage = "Attestation verification failed: possible tampering detected.",
                    Duration = sw.Elapsed,
                    Attestation = sandboxResult.Attestation
                };
            }
        }

        sw.Stop();

        await _notifier.NotifySandboxStatusAsync(
            _executionContext.CurrentPlanId ?? new PlanId(Guid.Empty), step.Id, config.ToolName, isolationLevel,
            sandboxResult.ResourceUsage ?? new ResourceUsage(),
            sandboxResult.Attestation?.Signature, ct);

        if (sandboxResult.Success)
        {
            var sanitizedOutput = sandboxResult.Output;
            if (!string.IsNullOrEmpty(sanitizedOutput))
            {
                var sanitizationResult = _responseSanitizer.Sanitize(sanitizedOutput, config.ToolName);
                sanitizedOutput = sanitizationResult.SanitizedContent;
            }

            return new StepExecutionResult
            {
                Status = StepExecutionStatus.Completed,
                Output = sanitizedOutput,
                Duration = sw.Elapsed,
                Attestation = sandboxResult.Attestation
            };
        }

        // Same treatment as the throw path above: the sandbox's failure text is raw process stderr, a
        // raw exception message, or raw container logs, and step error state is persisted and returned to
        // callers. Log it in full, persist only the stable code.
        _logger.LogWarning(
            "Tool {Tool} in step {Step} failed in the sandbox: {SandboxError}",
            config.ToolName, step.Name, sandboxResult.ErrorMessage ?? "(no detail reported)");

        return new StepExecutionResult
        {
            Status = StepExecutionStatus.Failed,
            ErrorMessage = PlanStepErrors.ToolFailed,
            Duration = sw.Elapsed,
            Attestation = sandboxResult.Attestation
        };
    }

    /// <summary>
    /// Authorizes the tool call through the invocation governor and the host's observer chain and,
    /// when denied, produces the failed step result. Returns null when the call is allowed. The
    /// denial message is already scrubbed for model/caller consumption (rule ids and policy internals
    /// stay in the governance trace and structured log), so it is safe to surface as the step error.
    /// </summary>
    /// <remarks>
    /// A plan step is a tool call like any other. Authorizing through the governor alone would let a
    /// plan reach a tool that the host's own rules refuse on the agent's conversational path — the
    /// agent could bypass a consumer's rule simply by emitting a plan step instead of calling the
    /// tool directly.
    /// </remarks>
    private async Task<StepExecutionResult?> AuthorizeToolAsync(
        string toolName,
        string stepName,
        IReadOnlyDictionary<string, object?> arguments,
        Stopwatch sw,
        CancellationToken ct)
    {
        var decision = await _toolInvocationGovernor
            .AuthorizeWithObserversAsync(_observers, toolName, arguments, ct);
        if (decision.IsAllowed)
            return null;

        sw.Stop();
        _logger.LogWarning(
            "Tool {Tool} denied by invocation governor in step {Step}", toolName, stepName);
        return new StepExecutionResult
        {
            Status = StepExecutionStatus.Failed,
            ErrorMessage = decision.DeniedMessage ?? GovernanceDenials.NotPermitted(toolName),
            Duration = sw.Elapsed,
            IsPolicyDenial = true
        };
    }

    private static SandboxIsolationLevel DetermineIsolation(
        ToolUseConfig config,
        ToolPermissionProfile profile,
        PlanStep step)
    {
        var level = profile.MinimumIsolation;

        if (config.IsolationLevelOverride.HasValue && config.IsolationLevelOverride.Value > level)
            level = config.IsolationLevelOverride.Value;

        if (step.RequiredAutonomyLevel is AutonomyLevel.Supervised or AutonomyLevel.Restricted)
        {
            if (level < SandboxIsolationLevel.Container)
                level = SandboxIsolationLevel.Container;
        }

        // Floor None to Process: no ISandboxExecutor is keyed for None (only Process and
        // Container are registered). A tool without a [ToolCapability] attribute resolves to
        // a profile with MinimumIsolation = None, which would otherwise throw
        // InvalidOperationException at keyed-service resolution. Process is the default
        // subprocess executor and the safe minimum for "direct-execution" tools.
        if (level < SandboxIsolationLevel.Process)
            level = SandboxIsolationLevel.Process;

        return level;
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
