using Application.AI.Common.Interfaces.Sandbox;
using Domain.AI.Sandbox;

namespace Infrastructure.AI.Tests.Support;

/// <summary>
/// Fake <see cref="ISandboxExecutor"/> that violates its own non-nullable <c>ExecuteAsync</c>
/// contract, modeling a misbehaving template consumer's custom executor. Shared by the Iac and
/// Workspace test suites, which used to each keep a byte-for-byte private copy of this same fake.
/// </summary>
internal sealed class NullReturningSandboxExecutor : ISandboxExecutor
{
    public Task<SandboxExecutionResult> ExecuteAsync(SandboxExecutionRequest request, CancellationToken ct)
        => Task.FromResult<SandboxExecutionResult>(null!);
}
