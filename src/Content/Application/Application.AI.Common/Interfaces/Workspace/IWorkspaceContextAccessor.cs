using Domain.AI.Workspace;

namespace Application.AI.Common.Interfaces.Workspace;

/// <summary>
/// Ambient accessor that exposes the <see cref="WorkspaceContext"/> for the
/// current sandbox-injected workspace turn. Set when the sandbox harness
/// establishes a workspace scope; cleared when the scope ends. Consumed by the
/// workspace tools (<c>read_file</c>, <c>write_file</c>, <c>list_files</c>,
/// <c>run_tests</c>, <c>run_lint</c>) so the working-copy path never threads
/// through tool method signatures.
/// </summary>
/// <remarks>
/// <para>
/// The accessor mirrors <see cref="Skills.ICurrentSkillAccessor"/>: an
/// <see cref="System.Threading.AsyncLocal{T}"/> backing store ensures the value
/// flows down the async call chain into MediatR handlers, delegating handlers,
/// and background continuations launched inside the workspace scope. Concurrent
/// agent turns running on different async contexts each see their own value.
/// </para>
/// <para>
/// A null <see cref="CurrentWorkspace"/> means "no workspace active" — workspace
/// tools must fail loudly in that state rather than silently fall back to the
/// host filesystem. The sandbox-required guarantee on the workspace skill
/// depends on this: if the ambient is unset, the tool is being invoked outside
/// the sandbox harness and must refuse.
/// </para>
/// </remarks>
public interface IWorkspaceContextAccessor
{
    /// <summary>
    /// Gets the workspace context active on this async context, or null when no
    /// workspace scope has been established.
    /// </summary>
    WorkspaceContext? CurrentWorkspace { get; }

    /// <summary>
    /// Establishes the supplied <paramref name="workspace"/> as the current
    /// workspace for this async context until the returned token is disposed.
    /// Restores the previous value on disposal so nested scopes compose.
    /// </summary>
    /// <param name="workspace">The workspace to make current. Must not be null.</param>
    /// <returns>A token that restores the previous workspace on disposal.</returns>
    /// <exception cref="System.ArgumentNullException">When <paramref name="workspace"/> is null.</exception>
    System.IDisposable BeginScope(WorkspaceContext workspace);
}
