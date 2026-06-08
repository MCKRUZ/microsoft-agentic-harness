using Application.AI.Common.Interfaces.Workspace;
using Domain.AI.Workspace;

namespace Infrastructure.AI.Tools.Workspace;

/// <summary>
/// <see cref="AsyncLocal{T}"/>-backed implementation of
/// <see cref="IWorkspaceContextAccessor"/>. The value flows down the async call
/// chain so workspace tools (<c>read_file</c>, <c>write_file</c>,
/// <c>list_files</c>, <c>run_tests</c>, <c>run_lint</c>) see the
/// sandbox-injected <see cref="WorkspaceContext"/> from anywhere in the
/// workspace scope without threading it through method signatures.
/// </summary>
/// <remarks>
/// Registered as a singleton — the backing store is per-async-flow, not
/// per-DI-scope. Concurrent agent turns each see their own value; nested
/// activations restore the previous context on disposal.
/// </remarks>
public sealed class WorkspaceContextAccessor : IWorkspaceContextAccessor
{
    private static readonly AsyncLocal<WorkspaceContext?> Slot = new();

    /// <inheritdoc />
    public WorkspaceContext? CurrentWorkspace => Slot.Value;

    /// <inheritdoc />
    public IDisposable BeginScope(WorkspaceContext workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        var previous = Slot.Value;
        Slot.Value = workspace;
        return new Restorer(previous);
    }

    private sealed class Restorer : IDisposable
    {
        private readonly WorkspaceContext? _previous;
        private bool _disposed;

        public Restorer(WorkspaceContext? previous)
        {
            _previous = previous;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            Slot.Value = _previous;
        }
    }
}
