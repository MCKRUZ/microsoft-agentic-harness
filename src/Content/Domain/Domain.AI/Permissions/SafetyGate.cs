namespace Domain.AI.Permissions;

/// <summary>
/// Defines a safety gate -- a path or operation pattern that always requires explicit approval,
/// regardless of bypass modes or allow rules. Safety gates are bypass-immune by design.
/// </summary>
/// <param name="PathPattern">Glob pattern for paths that trigger this gate (e.g., ".git/", ".ssh/").</param>
/// <param name="Description">Human-readable description of why this gate exists.</param>
public sealed record SafetyGate(string PathPattern, string Description)
{
    /// <summary>Safety gates are always bypass-immune.</summary>
    public bool IsBypassImmune => true;
}
