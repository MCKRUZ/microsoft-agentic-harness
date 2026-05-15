namespace Domain.AI.Sandbox;

/// <summary>
/// Runtime resource usage metrics captured during sandboxed tool execution.
/// </summary>
public sealed record ResourceUsage
{
    /// <summary>Memory consumed in bytes.</summary>
    public long MemoryBytes { get; init; }

    /// <summary>CPU time consumed in seconds.</summary>
    public double CpuTimeSeconds { get; init; }

    /// <summary>Wall-clock execution duration.</summary>
    public TimeSpan WallClockDuration { get; init; }

    /// <summary>Number of child processes spawned.</summary>
    public int SubprocessCount { get; init; }

    /// <summary>Disk space consumed in bytes.</summary>
    public long DiskUsageBytes { get; init; }
}
