using System.Diagnostics;
using System.Runtime.InteropServices;
using Application.AI.Common.Interfaces.Sandbox;
using Domain.AI.Sandbox;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Sandbox;

/// <summary>
/// No-op implementation of <see cref="IProcessResourceLimiter"/> for non-Windows platforms.
/// Logs a warning on each <see cref="Apply"/> call; returns null usage.
/// Use container isolation (Section 08) for cross-platform resource enforcement.
/// </summary>
public sealed class NoOpProcessResourceLimiter : IProcessResourceLimiter
{
    private readonly ILogger<NoOpProcessResourceLimiter> _logger;

    public NoOpProcessResourceLimiter(ILogger<NoOpProcessResourceLimiter> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public bool IsSupported => false;

    /// <inheritdoc />
    public bool Apply(Process process, ResourceLimits limits)
    {
        _logger.LogWarning(
            "Process resource limits not available on {OS}. Use container isolation for resource enforcement",
            RuntimeInformation.OSDescription);
        return false;
    }

    /// <inheritdoc />
    public ResourceUsage? GetUsage() => null;

    /// <inheritdoc />
    public void Dispose() { }
}
