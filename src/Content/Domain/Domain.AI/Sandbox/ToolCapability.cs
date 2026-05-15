namespace Domain.AI.Sandbox;

/// <summary>
/// Flags enum declaring the capabilities a tool requires to execute.
/// Used by the capability enforcer to verify that a tool's requirements
/// are satisfied before allowing execution.
/// </summary>
[Flags]
public enum ToolCapability
{
    /// <summary>No capabilities required.</summary>
    None           = 0,

    /// <summary>Read access to the filesystem.</summary>
    FileRead       = 1 << 0,

    /// <summary>Write access to the filesystem.</summary>
    FileWrite      = 1 << 1,

    /// <summary>Outbound network access (HTTP, TCP, etc.).</summary>
    NetworkAccess  = 1 << 2,

    /// <summary>Ability to spawn child processes.</summary>
    Subprocess     = 1 << 3,

    /// <summary>Read access to environment variables.</summary>
    EnvRead        = 1 << 4,

    /// <summary>Read access to databases.</summary>
    DatabaseRead   = 1 << 5,

    /// <summary>Write access to databases.</summary>
    DatabaseWrite  = 1 << 6,

    /// <summary>Ability to invoke LLM inference endpoints.</summary>
    LlmInvocation  = 1 << 7
}
