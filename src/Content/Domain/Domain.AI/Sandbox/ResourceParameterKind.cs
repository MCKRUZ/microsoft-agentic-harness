namespace Domain.AI.Sandbox;

/// <summary>
/// What a tool operation's named parameter represents, for the per-tool path/host capability
/// scoping <see cref="ToolPermissionProfile"/> carries (#418). Deliberately narrow — this is not a
/// general parameter-type system, only the two resource kinds <see cref="ToolPermissionProfile"/>
/// has allow/deny lists for.
/// </summary>
public enum ResourceParameterKind
{
    /// <summary>The parameter's value is a filesystem path.</summary>
    Path,

    /// <summary>The parameter's value is a network host (optionally with a port).</summary>
    Host
}
