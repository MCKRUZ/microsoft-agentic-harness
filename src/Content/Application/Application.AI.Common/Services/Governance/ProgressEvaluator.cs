using System.Diagnostics;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.OpenTelemetry.Metrics;
using Domain.AI.Telemetry.Conventions;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.Governance;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Application.AI.Common.Services.Governance;

/// <summary>
/// Scoped, deterministic spin / no-progress detector for the agent's live tool-call path. Counts the
/// sequence of tool-call signatures within a turn and breaks the loop when the agent is repeating an
/// identical call or making a run of calls that introduce nothing new.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Opt-in.</strong> Inert unless <c>GovernanceConfig.ProgressGuard.Enabled</c> is true — when
/// off, <see cref="Evaluate"/> records nothing and always returns <see cref="ProgressVerdict.Continue"/>,
/// so default deployments are unchanged.
/// </para>
/// <para>
/// Two independent detectors run on every call:
/// <list type="number">
///   <item><description><b>Repetition</b> — the identical signature (tool + arguments) fired
///     <c>RepetitionThreshold</c> times consecutively. Catches tight loops immediately.</description></item>
///   <item><description><b>No-progress</b> — <c>NoProgressWindow</c> calls in a row each repeated a
///     previously-seen signature, so no new information has been introduced. Catches multi-tool cycles
///     (A→B→A→B…) the consecutive detector misses.</description></item>
/// </list>
/// Both signals are defensible: re-issuing a call already made yields no new information by definition.
/// </para>
/// <para>
/// A previously-seen signature the agent abandons in favour of a genuinely new call resets the
/// no-progress counter, so the guard never blocks an agent that is still exploring.
/// </para>
/// <para>
/// <strong>The lock spans reading the counters and updating them, and must keep doing so.</strong>
/// Tool calls arrive in parallel batches, so a version that read them, released, and wrote back later
/// would let every member of a batch decide against the same stale state and admit the lot. See
/// <see cref="IProgressEvaluator"/>. Publishing a detected spin happens outside the lock — that is
/// observability, not detection state.
/// </para>
/// </remarks>
public sealed class ProgressEvaluator : IProgressEvaluator
{
    /// <summary>
    /// The escalation reason code raised on the governance trace when a spin is detected while
    /// configured for <see cref="ProgressGuardAction.Escalate"/>. A consumer eval can assert it via the
    /// <c>governance.behavior</c> metric's <c>expect_escalation</c> parameter.
    /// </summary>
    public const string SpinEscalationReasonCode = "progress.spin_detected";

    // Unit-separator (U+001F) between the tool name and the arguments signature so distinct
    // (tool, args) pairs cannot collide. Built from a char code to keep the source ASCII.
    private static readonly string ToolArgsSeparator = ((char)0x1F).ToString();

    private readonly IOptionsMonitor<GovernanceConfig> _governanceConfig;
    private readonly IGovernanceTraceRecorder _trace;
    private readonly ILogger<ProgressEvaluator> _logger;

    private readonly object _lock = new();
    private readonly HashSet<string> _seenSignatures = new(StringComparer.Ordinal);
    private string? _lastSignature;
    private int _consecutiveCount;
    private int _callsSinceNewSignature;

    /// <summary>Initializes a new instance of the <see cref="ProgressEvaluator"/> class.</summary>
    /// <param name="governanceConfig">Supplies the guard's opt-in switch and its two thresholds.</param>
    /// <param name="trace">Receives the escalation reason code when a spin is detected in escalate mode.</param>
    /// <param name="logger">Records a broken loop for operators.</param>
    public ProgressEvaluator(
        IOptionsMonitor<GovernanceConfig> governanceConfig,
        IGovernanceTraceRecorder trace,
        ILogger<ProgressEvaluator> logger)
    {
        ArgumentNullException.ThrowIfNull(governanceConfig);
        ArgumentNullException.ThrowIfNull(trace);
        ArgumentNullException.ThrowIfNull(logger);

        _governanceConfig = governanceConfig;
        _trace = trace;
        _logger = logger;
    }

    /// <inheritdoc />
    public ProgressVerdict Evaluate(string toolName, Func<string?> argumentsSignatureFactory)
    {
        ArgumentNullException.ThrowIfNull(argumentsSignatureFactory);

        var guard = _governanceConfig.CurrentValue.ProgressGuard;
        if (!guard.Enabled)
            return ProgressVerdict.Continue();

        // Factory invoked only past the enabled-gate, so the disabled (default) path never pays the
        // argument-serialisation cost on the hot tool-call path.
        var signature = string.Concat(toolName, ToolArgsSeparator, argumentsSignatureFactory() ?? string.Empty);

        // Set inside the lock, acted on outside it. Null means no spin.
        string? spinReason = null;

        lock (_lock)
        {
            // Consecutive-repetition counter.
            if (string.Equals(signature, _lastSignature, StringComparison.Ordinal))
            {
                _consecutiveCount++;
            }
            else
            {
                _consecutiveCount = 1;
                _lastSignature = signature;
            }

            // No-progress counter: resets the instant a genuinely new signature appears.
            if (_seenSignatures.Add(signature))
                _callsSinceNewSignature = 0;
            else
                _callsSinceNewSignature++;

            // Repetition is the more specific / faster signal, so check it first.
            if (guard.RepetitionThreshold >= 2 && _consecutiveCount >= guard.RepetitionThreshold)
                spinReason = GovernanceConventions.SpinReasonValues.Repetition;
            else if (guard.NoProgressWindow >= 2 && _callsSinceNewSignature >= guard.NoProgressWindow)
                spinReason = GovernanceConventions.SpinReasonValues.NoProgress;
        }

        // Publishing a spin happens outside the lock on purpose. It writes to the shared governance
        // trail, which has a lock of its own, and this critical section is the one a whole parallel
        // batch of tool calls queues behind — nesting another component's lock inside it lengthens the
        // section for every waiting call and couples two lock orders for no benefit. Nothing here
        // touches the counters.
        return spinReason is null
            ? ProgressVerdict.Continue()
            : PublishSpin(spinReason, toolName, guard.OnSpin);
    }

    /// <summary>
    /// Publishes a detected spin (metric + structured log, and an escalation reason code on the turn's
    /// governance trace when configured for <see cref="ProgressGuardAction.Escalate"/>) and returns the
    /// halt verdict. Called <em>without</em> holding <see cref="_lock"/> — it touches none of the
    /// counters, and it writes to a component with a lock of its own.
    /// </summary>
    private ProgressVerdict PublishSpin(string reason, string toolName, ProgressGuardAction action)
    {
        var mode = action == ProgressGuardAction.Escalate
            ? GovernanceConventions.SpinModeValues.Escalate
            : GovernanceConventions.SpinModeValues.Stop;

        // Onto the shared turn trail, which is where the turn handler reads escalations from. It used
        // to be a property of this type that the admission chain had to remember to fold in.
        if (action == ProgressGuardAction.Escalate)
            _trace.RecordEscalation(SpinEscalationReasonCode);

        GovernanceMetrics.SpinInterventions.Add(1, new TagList
        {
            { GovernanceConventions.SpinReasonTag, reason },
            { GovernanceConventions.SpinModeTag, mode },
            { GovernanceConventions.ToolName, toolName }
        });

        _logger.LogWarning(
            "Progress guard broke the agent loop on tool {ToolName}: {Reason} (mode {Mode})",
            toolName, reason, mode);

        // Model-facing message is deliberately generic and actionable — it tells the model the loop was
        // broken and how to proceed, without leaking thresholds or internal detail.
        return ProgressVerdict.Halt(
            $"Error: tool '{toolName}' was stopped because this action is repeating without making " +
            "progress. Change your approach, try a different action, or summarize what you have so far.");
    }

    /// <inheritdoc />
    public void Reset()
    {
        lock (_lock)
        {
            _seenSignatures.Clear();
            _lastSignature = null;
            _consecutiveCount = 0;
            _callsSinceNewSignature = 0;
        }
    }
}
