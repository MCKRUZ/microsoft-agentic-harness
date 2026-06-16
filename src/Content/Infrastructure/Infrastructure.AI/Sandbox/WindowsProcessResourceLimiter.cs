using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Application.AI.Common.Interfaces.Sandbox;
using Domain.AI.Sandbox;

namespace Infrastructure.AI.Sandbox;

/// <summary>
/// Windows implementation of <see cref="IProcessResourceLimiter"/> using Job Objects.
/// Each process gets its own Job Object, tracked by process ID.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsProcessResourceLimiter : IProcessResourceLimiter
{
    private readonly ConcurrentDictionary<int, WindowsJobObjectManager> _managers = new();

    /// <inheritdoc />
    public bool IsSupported => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    /// <inheritdoc />
    public bool Apply(Process process, ResourceLimits limits)
    {
        if (!IsSupported) return false;

        var manager = new WindowsJobObjectManager();
        try
        {
            manager.SetLimits(limits);
            manager.AssignProcess(process);
        }
        catch
        {
            manager.Dispose();
            throw;
        }

        if (_managers.TryRemove(process.Id, out var old))
            old.Dispose();

        _managers[process.Id] = manager;

        return true;
    }

    /// <inheritdoc />
    public ResourceUsage? GetUsage(int processId)
    {
        if (_managers.TryGetValue(processId, out var manager))
        {
            try { return manager.QueryUsage(); }
            catch { return null; }
        }
        return null;
    }

    /// <inheritdoc />
    public void Release(int processId)
    {
        if (_managers.TryRemove(processId, out var manager))
            manager.Dispose();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var kvp in _managers)
            kvp.Value.Dispose();
        _managers.Clear();
    }
}
