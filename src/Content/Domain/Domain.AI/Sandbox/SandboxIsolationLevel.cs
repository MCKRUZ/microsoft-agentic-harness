namespace Domain.AI.Sandbox;

/// <summary>
/// Defines the isolation level for sandbox execution. Numeric ordering is intentional —
/// higher values represent stricter isolation. The capability enforcer uses
/// <c>(int)Container > (int)Process > (int)None</c> for never-downgrade checks.
/// </summary>
public enum SandboxIsolationLevel
{
    /// <summary>Direct execution with no sandboxing (existing behavior for safe, read-only tools).</summary>
    None = 0,

    /// <summary>Subprocess execution with Job Object resource limits (default).</summary>
    Process = 1,

    /// <summary>Docker container with full filesystem, network, memory, and CPU isolation.</summary>
    Container = 2
}
