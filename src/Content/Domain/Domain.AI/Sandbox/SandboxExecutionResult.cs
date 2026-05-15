using Domain.AI.Attestation;

namespace Domain.AI.Sandbox;

/// <summary>
/// Result of executing a tool in a sandboxed environment, including
/// output, resource usage, and attestation.
/// </summary>
public sealed record SandboxExecutionResult
{
    /// <summary>Whether the tool executed successfully.</summary>
    public required bool Success { get; init; }

    /// <summary>Tool output. Null if execution failed before producing output.</summary>
    public string? Output { get; init; }

    /// <summary>Error message if execution failed. Null on success.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Process exit code. Null if the process was killed or never started.</summary>
    public int? ExitCode { get; init; }

    /// <summary>Resource usage during execution.</summary>
    public ResourceUsage? ResourceUsage { get; init; }

    /// <summary>HMAC-signed attestation of the execution.</summary>
    public ToolExecutionAttestation? Attestation { get; init; }
}
