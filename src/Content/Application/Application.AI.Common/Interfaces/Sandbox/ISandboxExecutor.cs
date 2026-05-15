using Domain.AI.Sandbox;

namespace Application.AI.Common.Interfaces.Sandbox;

/// <summary>
/// Executes a tool in an isolated sandbox environment. Two implementations are
/// registered via keyed DI on <see cref="SandboxIsolationLevel"/>:
/// <c>Process</c> (subprocess with Job Object limits) and <c>Container</c> (Docker).
/// </summary>
public interface ISandboxExecutor
{
    /// <summary>
    /// Executes the tool described by <paramref name="request"/> in a sandboxed environment.
    /// </summary>
    /// <param name="request">Execution request containing tool name, input, limits, and permissions.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Execution result including output, resource usage, and attestation.</returns>
    Task<SandboxExecutionResult> ExecuteAsync(SandboxExecutionRequest request, CancellationToken ct);
}
