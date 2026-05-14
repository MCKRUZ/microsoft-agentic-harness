namespace Domain.AI.Telemetry.Conventions;

/// <summary>Supervisor and delegation telemetry attribute names and metric identifiers.</summary>
public static class SupervisorConventions
{
    public const string SupervisorId = "agent.supervisor.id";
    public const string DelegateAgentId = "agent.supervisor.delegate_agent_id";
    public const string Outcome = "agent.supervisor.outcome";
    public const string AttemptedAction = "agent.supervisor.attempted_action";
    public const string CurrentTier = "agent.supervisor.current_tier";
    public const string AgentId = "agent.supervisor.agent_id";

    public const string DelegationsTotal = "agent.supervisor.delegations.total";
    public const string DelegationDuration = "agent.supervisor.delegations.duration_ms";
    public const string AutonomyExceededTotal = "agent.supervisor.autonomy.exceeded_total";
    public const string SelectionScore = "agent.supervisor.selection_score";
}
