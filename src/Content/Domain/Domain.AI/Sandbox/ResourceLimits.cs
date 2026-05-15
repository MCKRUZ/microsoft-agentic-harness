namespace Domain.AI.Sandbox;

/// <summary>
/// Resource constraints applied to sandboxed tool execution. Enforced by the
/// process sandbox via Job Objects (Windows) or cgroup limits (Linux),
/// and by the Docker sandbox via container configuration.
/// </summary>
public sealed record ResourceLimits
{
    /// <summary>Maximum memory in bytes. Default: 256 MB.</summary>
    public long MemoryLimitBytes { get; init; } = 256 * 1024 * 1024;

    /// <summary>Maximum CPU time in seconds. Default: 30 seconds.</summary>
    public double CpuTimeSeconds { get; init; } = 30;

    /// <summary>Maximum number of child processes. Default: 5.</summary>
    public int MaxSubprocesses { get; init; } = 5;

    /// <summary>Maximum disk usage in bytes. Default: 100 MB.</summary>
    public long DiskQuotaBytes { get; init; } = 100 * 1024 * 1024;
}
