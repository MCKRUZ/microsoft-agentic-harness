using Application.AI.Common.Interfaces.Governance;
using Domain.AI.Governance;
using Domain.Common.Config.AI;
using Microsoft.Extensions.Options;

namespace Application.AI.Common.Services.Governance;

/// <summary>
/// Default <see cref="IGovernanceTraceRecorder"/>: a thread-safe, per-turn collector of governance
/// decisions and escalation codes that emits an immutable <see cref="GovernanceTrace"/> on demand.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately holds no judgement of its own. It does not decide what a decision means, which stage
/// was entitled to record it, or whether a call should have been allowed — it stores what it is told
/// and reports it back. That is what makes it constructible in a test with one argument, which is the
/// point: asserting on an audit trail should not require standing up the machinery that produces one.
/// </para>
/// <para>
/// The lock covers the two collections only. The enforcement question is read outside it — it touches
/// nothing the lock protects, and holding a lock across a configuration lookup and an ambient-state
/// read would lengthen a critical section that a parallel batch of tool calls contends on.
/// </para>
/// </remarks>
public sealed class GovernanceTraceRecorder : IGovernanceTraceRecorder
{
    private readonly IOptionsMonitor<GovernanceConfig> _governanceConfig;

    private readonly object _lock = new();
    private readonly List<ToolDecisionRecord> _decisions = [];

    // Case-insensitive to honour GovernanceTrace.EscalationReasonCodes' "distinct" contract and to
    // stay aligned with GovernanceTrace.Merge, which unions codes the same way when folding per-turn
    // traces into a conversation.
    private readonly HashSet<string> _escalations = new(StringComparer.OrdinalIgnoreCase);

    // A one-way latch within a turn, cleared only at a turn boundary. Volatile rather than lock-guarded
    // so the every-tool-call write (MarkEnforced) costs nothing on the governed path.
    private volatile bool _enforcementObserved;

    /// <summary>Initializes a new instance of the <see cref="GovernanceTraceRecorder"/> class.</summary>
    /// <param name="governanceConfig">Supplies the live "is governance on for this flow" signal.</param>
    public GovernanceTraceRecorder(IOptionsMonitor<GovernanceConfig> governanceConfig)
    {
        ArgumentNullException.ThrowIfNull(governanceConfig);
        _governanceConfig = governanceConfig;
    }

    /// <inheritdoc />
    public bool EnforcementEnabled =>
        // Short-circuits before the configuration and ambient-state reads on the common governed path.
        _enforcementObserved || GovernanceEnforcement.IsActive(_governanceConfig.CurrentValue);

    /// <inheritdoc />
    public void MarkEnforced() => _enforcementObserved = true;

    /// <inheritdoc />
    public void Record(ToolDecisionRecord decision)
    {
        ArgumentNullException.ThrowIfNull(decision);

        lock (_lock)
            _decisions.Add(decision);
    }

    /// <inheritdoc />
    public void RecordEscalation(string reasonCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reasonCode);

        lock (_lock)
            _escalations.Add(reasonCode);
    }

    /// <inheritdoc />
    public GovernanceTrace Snapshot()
    {
        var enforced = EnforcementEnabled;

        lock (_lock)
        {
            // The shared empty instance, by reference, when there is genuinely nothing to report. An
            // ungoverned turn that recorded nothing is the overwhelmingly common case — the default
            // composition never enforces — and callers distinguish it by identity.
            if (!enforced && _decisions.Count == 0 && _escalations.Count == 0)
                return GovernanceTrace.Empty;

            return new GovernanceTrace
            {
                EnforcementEnabled = enforced,
                ToolDecisions = [.. _decisions],
                EscalationReasonCodes = [.. _escalations]
            };
        }
    }

    /// <inheritdoc />
    public void Reset()
    {
        _enforcementObserved = false;

        lock (_lock)
        {
            _decisions.Clear();
            _escalations.Clear();
        }
    }
}
