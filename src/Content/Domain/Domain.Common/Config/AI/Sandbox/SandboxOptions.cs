namespace Domain.Common.Config.AI.Sandbox;

/// <summary>
/// System-level configuration for the sandbox execution subsystem.
/// Bound from <c>AppConfig:AI:Sandbox</c> in appsettings.json.
/// </summary>
/// <remarks>
/// Distinct from <see cref="SandboxConfig"/> which controls capability/permission enforcement.
/// This class controls resource limits and execution environment defaults.
/// </remarks>
public sealed class SandboxOptions
{
    /// <summary>Master toggle for the sandbox subsystem.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Default isolation level name when a tool has no explicit minimum.
    /// Parsed to <c>SandboxIsolationLevel</c> at runtime. Valid values: "Process", "Container".
    /// </summary>
    public string DefaultIsolationLevel { get; set; } = "Process";

    /// <summary>Default memory limit in MB for sandboxed execution.</summary>
    public int DefaultMemoryLimitMb { get; set; } = 256;

    /// <summary>Default CPU time limit in seconds.</summary>
    public double DefaultCpuTimeSeconds { get; set; } = 30;

    /// <summary>Default maximum child processes per tool execution.</summary>
    public int DefaultMaxSubprocesses { get; set; } = 5;

    /// <summary>Default disk quota in MB for workspace directories.</summary>
    public int DefaultDiskQuotaMb { get; set; } = 100;

    /// <summary>Default execution timeout in seconds.</summary>
    public int DefaultTimeoutSeconds { get; set; } = 60;

    /// <summary>Docker container defaults for container-isolated execution.</summary>
    public ContainerDefaultsConfig ContainerDefaults { get; set; } = new();

    /// <summary>Per-tool resource and isolation overrides, keyed by tool name.</summary>
    public Dictionary<string, ToolOverrideConfig> ToolOverrides { get; set; } = new();
}
