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
    private readonly ILogger<ProgressEvaluator> _logger;

    private readonly object _lock = new();
    private readonly HashSet<string> _seenSignatures = new(StringComparer.Ordinal);
    private bool _escalated;
    private string? _lastSignature;
    private int _consecutiveCount;
    private int _callsSinceNewSignature;

    public ProgressEvaluator(
        IOptionsMonitor<GovernanceConfig> governanceConfig,
        ILogger<ProgressEvaluator> logger)
    {
        _governanceConfig = governanceConfig;
        _logger = logger;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> EscalationReasonCodes
    {
        get
        {
            lock (_lock)
                return _escalated ? [SpinEscalationReasonCode] : [];
        }
    }

    /// <inheritdoc />
    public ProgressVerdict Evaluate(string toolName, Func<string?> argumentsSignatureFactory)
    {
        var guard = _governanceConfig.CurrentValue.ProgressGuard;
        if (!guard.Enabled)
            return ProgressVerdict.Continue();

        // Factory invoked only past the enabled-gate, so the disabled (default) path never pays the
        // argument-serialisation cost on the hot tool-call path.
        var signature = string.Concat(toolName, ToolArgsSeparator, argumentsSignatureFactory() ?? string.Empty);

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
                return RecordSpin(GovernanceConventions.SpinReasonValues.Repetition, toolName, guard.OnSpin);

            if (guard.NoProgressWindow >= 2 && _callsSinceNewSignature >= guard.NoProgressWindow)
                return RecordSpin(GovernanceConventions.SpinReasonValues.NoProgress, toolName, guard.OnSpin);

            return ProgressVerdict.Continue();
        }
    }

    /// <summary>
    /// Records a detected spin (metric + structured log, and an escalation reason code when configured
    /// for <see cref="ProgressGuardAction.Escalate"/>) and returns the halt verdict. Called while
    /// holding <see cref="_lock"/>.
    /// </summary>
    private ProgressVerdict RecordSpin(string reason, string toolName, ProgressGuardAction action)
    {
        var mode = action == ProgressGuardAction.Escalate
            ? GovernanceConventions.SpinModeValues.Escalate
            : GovernanceConventions.SpinModeValues.Stop;

        if (action == ProgressGuardAction.Escalate)
            _escalated = true;

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
            _escalated = false;
            _lastSignature = null;
            _consecutiveCount = 0;
            _callsSinceNewSignature = 0;
        }
    }
}
