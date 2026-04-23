namespace Domain.AI.Telemetry.Conventions;

/// <summary>Agent session telemetry attributes and metric names.</summary>
public static class SessionConventions
{
    /// <summary>Session health score (0=red, 1=yellow, 2=green). Tags: agent.name.</summary>
    public const string HealthScore = "agent.session.health_score";
    /// <summary>Currently active session count. Tags: agent.name.</summary>
    public const string Active = "agent.session.active";
    /// <summary>Session identifier dimension label.</summary>
    public const string SessionId = "agent.session.id";
}
