namespace Domain.AI.Telemetry.Conventions;

/// <summary>
/// OpenTelemetry semantic conventions for the tool permission system.
/// </summary>
public static class PermissionConventions
{
    /// <summary>The permission decision outcome (Allow, Deny, Ask).</summary>
    public const string Decision = "agent.permission.decision";

    /// <summary>The source of the matched rule.</summary>
    public const string RuleSource = "agent.permission.source";

    /// <summary>The tool name being permission-checked.</summary>
    public const string ToolName = "agent.permission.tool";

    /// <summary>Counter for total permission denials.</summary>
    public const string DenialCount = "agent.permission.denials";
}
