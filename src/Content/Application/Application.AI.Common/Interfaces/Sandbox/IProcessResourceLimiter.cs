using Domain.AI.Sandbox;

namespace Application.AI.Common.Interfaces.Sandbox;

/// <summary>
/// Applies OS-level resource limits to a running process and tracks resource usage.
/// On Windows, uses Job Objects via P/Invoke. Extends <see cref="IDisposable"/>
/// for Job Object handle cleanup.
/// </summary>
public interface IProcessResourceLimiter : IDisposable
{
    /// <summary>
    /// Applies the specified resource limits to the process.
    /// </summary>
    /// <param name="process">The process to constrain.</param>
    /// <param name="limits">Resource limits to apply.</param>
    /// <returns>True if limits were applied successfully; false if the platform doesn't support it.</returns>
    bool Apply(System.Diagnostics.Process process, ResourceLimits limits);

    /// <summary>
    /// Retrieves current resource usage of the process previously constrained via <see cref="Apply"/>.
    /// </summary>
    /// <param name="processId">The id of the constrained process to query.</param>
    /// <returns>Resource usage metrics, or null if usage tracking is not available for that process.</returns>
    ResourceUsage? GetUsage(int processId);

    /// <summary>
    /// Releases the OS-level resources (e.g. the Windows Job Object handle) associated with a
    /// completed process. Callers must invoke this once a constrained process has exited and its
    /// usage has been read; otherwise each execution leaks a kernel handle until host shutdown.
    /// Safe to call with an unknown id (no-op).
    /// </summary>
    /// <param name="processId">The id of the completed process to release.</param>
    void Release(int processId);

    /// <summary>
    /// Whether this limiter is supported on the current platform.
    /// Enables platform detection without try/catch.
    /// </summary>
    bool IsSupported { get; }
}
