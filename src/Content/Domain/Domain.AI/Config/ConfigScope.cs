namespace Domain.AI.Config;

/// <summary>
/// Defines the scope/priority level of a discovered configuration file.
/// Higher-scoped files (closer to working directory) take precedence.
/// </summary>
public enum ConfigScope
{
    /// <summary>System-managed configuration (lowest priority).</summary>
    Managed,

    /// <summary>User-level configuration (home directory).</summary>
    User,

    /// <summary>Project-level configuration (repository root).</summary>
    Project,

    /// <summary>Local configuration (not checked in, highest priority).</summary>
    Local
}
