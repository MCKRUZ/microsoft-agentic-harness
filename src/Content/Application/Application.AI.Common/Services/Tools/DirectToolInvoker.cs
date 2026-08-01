using System.Diagnostics;
using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Planner;
using Application.AI.Common.Interfaces.Tools;
using Application.AI.Common.Services.Governance;
using Domain.AI.Governance;
using Domain.AI.Models;
using Domain.Common.Config.AI.DirectToolInvocation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Application.AI.Common.Services.Tools;

/// <summary>
/// Default <see cref="IDirectToolInvoker"/>: the one place a tool is armed, authorized, executed and
/// sanitized on behalf of an HTTP caller.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Arming order is load-bearing: identity, then envelope, then the accessors.</strong>
/// <c>ToolInvocationGovernor</c> treats an armed envelope as proof that a governed external flow is in
/// progress and switches enforcement on for it; with enforcement on and no agent identity in the
/// execution context it denies, by design. Publishing the envelope before the identity would therefore
/// deny every invocation — and any future rearrangement that separates the two reintroduces exactly
/// that. This is the same order, for the same reason, as <c>PlanRunExecutor</c>.
/// </para>
/// <para>
/// <strong>The synthetic agent identity is not decoration.</strong> It is the subject permission rules
/// resolve against and the subject the governance audit records, so it must be stable per caller and
/// must not collide with a real agent's id — otherwise one caller's tool use is attributed to another,
/// or worse, inherits permission rules written for a named agent.
/// </para>
/// <para>
/// <strong>No sandbox routing, deliberately.</strong> Direct invocation has agent-path parity: it runs
/// the tool in-process exactly as an agent turn does, so self-sandboxing tools still sandbox themselves
/// and capability flags are still enforced by the governor. Routing through <c>ISandboxExecutor</c>
/// would be the plan path's posture, and adopting it here would mean a caller's direct invocation and
/// the same tool called from their own workflow behaved differently.
/// </para>
/// <para>
/// <strong>No tool-output compression, deliberately.</strong> Compression exists to fit results into a
/// model's context window and does so by summarising and substituting pointers the agent can expand.
/// An HTTP caller cannot expand them, so compression would hand them an unusable reference in place of
/// their answer. Output is bounded by truncation instead, and truncation is reported.
/// </para>
/// </remarks>
public sealed class DirectToolInvoker : IDirectToolInvoker
{
    /// <summary>
    /// Prefix for the synthetic agent identity minted per caller. The colon is inside the charset
    /// <see cref="PlanRunRequest.IsWellFormedAgentId"/> permits, and no configured agent is named this
    /// way, so a direct invocation cannot pick up permission rules authored for a real agent.
    /// </summary>
    private const string SyntheticAgentPrefix = "direct-invoke:";

    /// <summary>Marker appended to a truncated result so the cut is visible in the payload itself.</summary>
    internal const string TruncationMarker = "\n…[output truncated]";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IToolCatalog _catalog;
    private readonly ICompositeResponseSanitizer _sanitizer;
    private readonly IOptionsMonitor<DirectToolInvocationConfig> _config;
    private readonly ILogger<DirectToolInvoker> _logger;

    /// <summary>Initializes the invoker.</summary>
    /// <param name="scopeFactory">Creates the per-invocation DI scope holding the scoped governor and execution context.</param>
    /// <param name="catalog">Resolves the tool, filtered to the caller's grant.</param>
    /// <param name="sanitizer">Scrubs everything leaving the trust boundary.</param>
    /// <param name="config">The host's direct-invocation settings, read per request.</param>
    /// <param name="logger">Records denials, faults, and the governance trace.</param>
    public DirectToolInvoker(
        IServiceScopeFactory scopeFactory,
        IToolCatalog catalog,
        ICompositeResponseSanitizer sanitizer,
        IOptionsMonitor<DirectToolInvocationConfig> config,
        ILogger<DirectToolInvoker> logger)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(sanitizer);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);

        _scopeFactory = scopeFactory;
        _catalog = catalog;
        _sanitizer = sanitizer;
        _config = config;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<DirectToolInvocationOutcome> InvokeAsync(
        DirectToolInvocationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var config = _config.CurrentValue;
        if (!config.Enabled)
        {
            return DirectToolInvocationOutcome.Refused(
                DirectToolInvocationStatus.Disabled, "Direct tool invocation is not enabled on this host.");
        }

        var admission = Admit(request, config);
        if (admission.Refusal is { } refusal)
            return refusal;

        return await ExecuteAsync(request, admission, config, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Everything decidable before a scope is created: the request's shape, the caller's grant, and
    /// whether the named tool is offered on this surface at all.
    /// </summary>
    /// <remarks>
    /// Kept ahead of scope creation so a malformed or ungranted request costs a dictionary lookup
    /// rather than a DI scope and a governance evaluation.
    /// </remarks>
    private Admission Admit(DirectToolInvocationRequest request, DirectToolInvocationConfig config)
    {
        if (string.IsNullOrWhiteSpace(request.ToolName) || string.IsNullOrWhiteSpace(request.Operation))
            return Admission.Refuse(DirectToolInvocationStatus.Invalid, "A tool name and an operation are required.");

        if (request.Parameters.Count > config.MaxParameterCount)
        {
            return Admission.Refuse(
                DirectToolInvocationStatus.Invalid,
                $"An invocation may pass at most {config.MaxParameterCount} parameters.");
        }

        if (request.RequestedTimeout is { } requested)
        {
            if (requested <= TimeSpan.Zero || requested > config.InvocationTimeout)
            {
                // Refused rather than clamped: a caller silently given less time than they asked for
                // experiences a timeout with nothing in the response that accounts for it.
                return Admission.Refuse(
                    DirectToolInvocationStatus.Invalid,
                    $"Requested timeout must be positive and no greater than {config.InvocationTimeout}.");
            }
        }

        var agentId = SyntheticAgentPrefix + request.OwnerId;
        if (!PlanRunRequest.IsWellFormedAgentId(agentId))
        {
            // The identity is the permission subject and the audit subject. The rejected value is
            // deliberately not echoed or logged verbatim — it originates in a token claim.
            _logger.LogWarning(
                "Direct invocation rejected: caller identity is unusable as a permission subject (length {Length})",
                request.OwnerId?.Length ?? 0);
            return Admission.Refuse(
                DirectToolInvocationStatus.Invalid, "The caller's identity cannot be used as a permission subject.");
        }

        // FindGranted answers null for a tool that does not exist AND for one the envelope does not
        // grant — the two are indistinguishable on purpose. Adding "not offered on this surface" to
        // that set keeps the disclosure boundary at exactly one bit: reachable, or not.
        var descriptor = _catalog.FindGranted(request.ToolName, request.Envelope);
        if (descriptor is null || !descriptor.IsDirectlyInvocable)
            return Admission.Refuse(DirectToolInvocationStatus.NotFound, "No such tool is available to this caller.");

        if (!descriptor.SupportedOperations.Contains(request.Operation, StringComparer.OrdinalIgnoreCase))
        {
            // Naming the operations is not a disclosure: the caller is already entitled to this tool and
            // can read the same list from the catalog. Withholding it would only cost them a round trip.
            return Admission.Refuse(
                DirectToolInvocationStatus.Invalid,
                descriptor.SupportedOperations.Count == 0
                    ? $"Tool '{descriptor.Name}' declares no operations."
                    : $"Tool '{descriptor.Name}' supports: {string.Join(", ", descriptor.SupportedOperations)}.");
        }

        return Admission.Accept(descriptor.Name, agentId);
    }

    /// <summary>
    /// Arms the invocation and runs it. Every exit path tears down what it armed.
    /// </summary>
    private async Task<DirectToolInvocationOutcome> ExecuteAsync(
        DirectToolInvocationRequest request,
        Admission admission,
        DirectToolInvocationConfig config,
        CancellationToken cancellationToken)
    {
        var toolName = admission.ToolName!;
        var sw = Stopwatch.StartNew();

        await using var scope = _scopeFactory.CreateAsyncScope();

        try
        {
            // Identity FIRST. See the type remarks: the governor denies an identity-less call once an
            // envelope is armed, so reordering these two lines denies every invocation.
            var executionContext = scope.ServiceProvider.GetRequiredService<IAgentExecutionContext>();
            executionContext.Initialize(admission.AgentId!, InvocationScope(admission.AgentId!), turnNumber: 1);

            // Envelope SECOND. Arming it is what switches governance enforcement on for this flow.
            using var granted = CapabilityEnvelopeAccessor.Begin(request.Envelope);

            var governor = scope.ServiceProvider.GetRequiredService<IToolInvocationGovernor>();
            var classificationGate = scope.ServiceProvider.GetService<IToolClassificationGate>();

            governor.Reset();
            ToolGovernanceAccessor.Current = governor;
            ClassificationGateAccessor.Current = classificationGate;

            // The progress guard is deliberately NOT armed. It detects an agent repeating identical
            // calls across a turn, and a single invocation has no sequence to evaluate — arming a
            // fresh evaluator that can only ever see one call would be machinery that cannot fire.

            try
            {
                return await AuthorizeAndRunAsync(
                    request, toolName, governor, classificationGate, scope, config, sw, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                ToolGovernanceAccessor.Current = null;
                ClassificationGateAccessor.Current = null;
                LogTrace(toolName, admission.AgentId!, governor.GetTrace());
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller went away or the host is shutting down. Distinct from the deadline below:
            // nobody is waiting for an answer, so unwind rather than manufacture one.
            throw;
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            _logger.LogWarning(
                "Direct invocation of {ToolName} exceeded its {Timeout} deadline",
                toolName, request.RequestedTimeout ?? config.InvocationTimeout);
            return DirectToolInvocationOutcome.Refused(
                DirectToolInvocationStatus.TimedOut, "The tool did not complete within its deadline.", sw.Elapsed);
        }
        catch (Exception ex)
        {
            sw.Stop();
            // Full detail stays in the structured log. Tool and infrastructure exceptions carry host
            // paths, connection strings and container internals, and this response crosses a trust
            // boundary — so the caller gets a stable code and nothing derived from the exception.
            _logger.LogError(ex, "Direct invocation of {ToolName} threw", toolName);
            return DirectToolInvocationOutcome.Refused(
                DirectToolInvocationStatus.Faulted, "direct_tool_invocation.failed", sw.Elapsed);
        }
    }

    /// <summary>
    /// Runs the three gates the agent path runs — governance, classification, then the tool itself —
    /// and shapes the result for a caller outside the process.
    /// </summary>
    private async Task<DirectToolInvocationOutcome> AuthorizeAndRunAsync(
        DirectToolInvocationRequest request,
        string toolName,
        IToolInvocationGovernor governor,
        IToolClassificationGate? classificationGate,
        AsyncServiceScope scope,
        DirectToolInvocationConfig config,
        Stopwatch sw,
        CancellationToken cancellationToken)
    {
        var decision = await governor.AuthorizeAsync(toolName, cancellationToken).ConfigureAwait(false);
        if (!decision.IsAllowed)
        {
            sw.Stop();
            // The governor's message is already scrubbed for consumption outside the host — rule ids,
            // paths and policy internals stay in the trace and the structured log.
            return DirectToolInvocationOutcome.Refused(
                DirectToolInvocationStatus.Denied,
                decision.DeniedMessage ?? GovernanceDenials.NotPermitted(toolName),
                sw.Elapsed);
        }

        // The classification gate is consulted explicitly rather than inherited. On the agent path
        // GovernedAIFunction calls it, but that wrapper sits on the AIFunction the model invokes and
        // this path never builds one — so an armed-but-unconsulted gate would be a data-loss control
        // that silently does not apply to the surface most exposed to external callers.
        ClassificationVerdict? classification = null;
        if (classificationGate is not null)
        {
            classification = await classificationGate
                .EvaluateAsync(toolName, request.Parameters, cancellationToken).ConfigureAwait(false);

            if (classification.Outcome == ClassificationGateOutcome.Block)
            {
                sw.Stop();
                return DirectToolInvocationOutcome.Refused(
                    DirectToolInvocationStatus.Denied,
                    classification.BlockedMessage ?? GovernanceDenials.NotPermitted(toolName),
                    sw.Elapsed);
            }
        }

        var tool = scope.ServiceProvider.GetKeyedService<ITool>(toolName);
        if (tool is null)
        {
            // The catalog described it, so it resolved once. Losing it here means the host cannot
            // construct it in this scope — answered as absence, matching how the catalog omits a tool
            // it cannot build.
            sw.Stop();
            _logger.LogWarning("Tool {ToolName} is cataloged but did not resolve for invocation", toolName);
            return DirectToolInvocationOutcome.Refused(
                DirectToolInvocationStatus.NotFound, "No such tool is available to this caller.", sw.Elapsed);
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(request.RequestedTimeout ?? config.InvocationTimeout);

        var result = await tool
            .ExecuteAsync(request.Operation, request.Parameters, deadline.Token)
            .ConfigureAwait(false);

        sw.Stop();
        return Shape(result, toolName, classification, classificationGate, config, sw.Elapsed);
    }

    /// <summary>
    /// Turns a <see cref="ToolResult"/> into a caller-facing outcome: redact if classified, sanitize
    /// unconditionally, then bound the length.
    /// </summary>
    /// <remarks>
    /// Sanitization is applied to the failure message as well as the output. A tool's own error text is
    /// the likeliest place for a path or a connection string to surface, and it crosses the same
    /// boundary the output does — treating only success as sensitive would leave the more dangerous
    /// half unscrubbed.
    /// </remarks>
    private DirectToolInvocationOutcome Shape(
        ToolResult result,
        string toolName,
        ClassificationVerdict? classification,
        IToolClassificationGate? classificationGate,
        DirectToolInvocationConfig config,
        TimeSpan duration)
    {
        if (!result.Success)
        {
            return new DirectToolInvocationOutcome
            {
                Status = DirectToolInvocationStatus.ToolFailed,
                Error = Scrub(result.Error ?? "The tool reported a failure.", toolName),
                Duration = duration
            };
        }

        var output = result.Output ?? string.Empty;

        if (classification?.Outcome == ClassificationGateOutcome.RedactOutput && classificationGate is not null)
            output = classificationGate.RedactResult(toolName, output) as string ?? output;

        output = Scrub(output, toolName);

        var truncated = output.Length > config.MaxOutputCharacters;
        if (truncated)
            output = string.Concat(output.AsSpan(0, config.MaxOutputCharacters), TruncationMarker);

        return new DirectToolInvocationOutcome
        {
            Status = DirectToolInvocationStatus.Succeeded,
            Output = output,
            OutputTruncated = truncated,
            Duration = duration
        };
    }

    /// <summary>Runs text through the response-sanitizer chain. Empty text is returned untouched.</summary>
    private string Scrub(string content, string toolName) =>
        string.IsNullOrEmpty(content) ? content : _sanitizer.Sanitize(content, toolName).SanitizedContent;

    /// <summary>
    /// The conversation scope the execution context is bound to. Derived from the caller's synthetic
    /// agent id so every invocation by one caller shares a scope, and two callers never share one.
    /// </summary>
    private static string InvocationScope(string agentId) => agentId;

    /// <summary>
    /// Records what governance decided. The trace carries rule ids and policy reasons, so it is written
    /// to the host's log and never returned to the caller.
    /// </summary>
    private void LogTrace(string toolName, string agentId, GovernanceTrace trace)
    {
        if (trace.ToolDecisions.Count == 0)
            return;

        foreach (var decision in trace.ToolDecisions)
        {
            _logger.LogInformation(
                "Direct invocation governance: caller {AgentId} tool {ToolName} → {Outcome} ({Reason}); enforced={Enforced}",
                agentId, toolName, decision.Outcome, decision.Reason, decision.Enforced);
        }
    }

    /// <summary>
    /// The result of pre-execution admission: either a refusal to return as-is, or the resolved tool
    /// name and caller identity to run with.
    /// </summary>
    private readonly record struct Admission(
        DirectToolInvocationOutcome? Refusal, string? ToolName, string? AgentId)
    {
        public static Admission Refuse(DirectToolInvocationStatus status, string error) =>
            new(DirectToolInvocationOutcome.Refused(status, error), null, null);

        public static Admission Accept(string toolName, string agentId) => new(null, toolName, agentId);
    }
}
