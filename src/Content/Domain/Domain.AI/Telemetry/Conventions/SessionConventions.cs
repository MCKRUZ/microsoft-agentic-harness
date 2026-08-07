namespace Domain.AI.Telemetry.Conventions;

/// <summary>Agent session telemetry attributes and metric names.</summary>
public static class SessionConventions
{
    /// <summary>Session health score (0=red, 1=yellow, 2=green). Tags: agent.name.</summary>
    public const string HealthScore = "agent.session.health_score";
    // agent.session.active was removed in issue #289. Three transports incremented it while meaning
    // three different things — runs in flight, live connections, and conversations ever started — so
    // the sum was not a quantity. Replaced by OrchestrationConventions.RunsActive and
    // OrchestrationConventions.ConnectionsActive, each of which answers exactly one of them.

    /// <summary>Session identifier dimension label.</summary>
    public const string SessionId = "agent.session.id";
    /// <summary>Total USD cost for a completed session.</summary>
    public const string SessionCost = "agent.session.cost";
    /// <summary>Total sessions started.</summary>
    public const string SessionsStarted = "agent.session.started";
}
