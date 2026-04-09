namespace Domain.AI.Telemetry.Conventions;

/// <summary>
/// OpenTelemetry semantic conventions for the hook/event system.
/// </summary>
public static class HookConventions
{
    /// <summary>The hook event type that triggered execution.</summary>
    public const string EventType = "agent.hook.event";

    /// <summary>The hook execution mechanism type.</summary>
    public const string HookType = "agent.hook.type";

    /// <summary>The hook definition ID.</summary>
    public const string HookId = "agent.hook.id";

    /// <summary>Hook execution duration in milliseconds.</summary>
    public const string Duration = "agent.hook.duration_ms";

    /// <summary>Whether the hook allowed continuation.</summary>
    public const string Continued = "agent.hook.continued";
}
