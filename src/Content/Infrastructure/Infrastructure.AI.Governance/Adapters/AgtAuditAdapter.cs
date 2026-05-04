using AgentGovernance.Audit;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.OpenTelemetry.Metrics;
using Domain.AI.Telemetry.Conventions;

namespace Infrastructure.AI.Governance.Adapters;

/// <summary>Wraps the AGT <see cref="AuditLogger"/> behind the harness-owned <see cref="IGovernanceAuditService"/>.</summary>
internal sealed class AgtAuditAdapter : IGovernanceAuditService
{
    private readonly AuditLogger _logger;

    public AgtAuditAdapter(AuditLogger logger) => _logger = logger;

    public int EntryCount => _logger.Count;

    public void Log(string agentId, string action, string decision)
    {
        _logger.Log(agentId, action, decision);
        GovernanceMetrics.AuditEvents.Add(1,
            new KeyValuePair<string, object?>(GovernanceConventions.Action, action));
    }

    public bool VerifyChainIntegrity() => _logger.Verify();
}
