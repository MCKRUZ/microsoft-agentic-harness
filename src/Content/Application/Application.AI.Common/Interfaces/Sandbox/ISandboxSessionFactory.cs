using Domain.AI.Sandbox;
using Domain.Common;

namespace Application.AI.Common.Interfaces.Sandbox;

/// <summary>
/// Starts a long-lived sandboxed session. Two implementations are registered via keyed DI on
/// <see cref="SandboxIsolationLevel"/>, mirroring <see cref="ISandboxExecutor"/>: <c>Process</c>
/// (subprocess with Job Object limits) and <c>Container</c> (Docker).
/// </summary>
/// <remarks>
/// The two backends are not equally strong. <c>Container</c> is a genuine isolation boundary:
/// unprivileged user, all capabilities dropped, read-only root filesystem, no network unless
/// granted. <c>Process</c> is not — the session runs as the same OS user with the same file
/// access and no network restriction, and only a program the operator has explicitly allowlisted
/// may run this way. Callers that hand an untrusted, caller-supplied command to this factory
/// (rather than an operator-configured one) must require <c>Container</c>.
/// </remarks>
public interface ISandboxSessionFactory
{
    /// <summary>
    /// Starts a sandboxed session for the program described by <paramref name="request"/>.
    /// Returns a failure <see cref="Result{T}"/> — never throws — for expected failures: sandbox
    /// disabled, command not allowlisted, invalid resource limits, egress preflight denial, or
    /// backend unavailable (e.g. the Docker daemon is unreachable and container isolation was
    /// required).
    /// </summary>
    /// <param name="request">Session request containing tool name, limits, and permissions.</param>
    /// <param name="ct">Cancellation token covering session startup only, not the session's lifetime.</param>
    Task<Result<ISandboxSession>> StartSessionAsync(SandboxSessionRequest request, CancellationToken ct);
}
