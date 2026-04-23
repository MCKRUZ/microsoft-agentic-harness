namespace Domain.AI.Telemetry.Conventions;

/// <summary>User activity telemetry attributes and metric names.</summary>
public static class UserConventions
{
    /// <summary>Azure AD object ID of the authenticated user.</summary>
    public const string UserId = "user.id";

    /// <summary>Total turns initiated by a user. Tags: user.id, agent.name.</summary>
    public const string Turns = "user.activity.turns";

    /// <summary>Total tokens consumed by a user. Tags: user.id, agent.name.</summary>
    public const string TokensConsumed = "user.activity.tokens_consumed";

    /// <summary>Total estimated cost attributed to a user. Tags: user.id, agent.name.</summary>
    public const string CostAccrued = "user.activity.cost_accrued";

    /// <summary>Total sessions started by a user. Tags: user.id.</summary>
    public const string SessionsStarted = "user.activity.sessions_started";
}
