using Application.AI.Common.Interfaces.Sandbox;
using Domain.AI.Sandbox;

namespace Application.AI.Common.Extensions;

/// <summary>
/// Runs an <see cref="ISandboxExecutor"/> and enforces the non-nullable result its interface
/// declares but cannot compile-time-guarantee.
/// </summary>
/// <remarks>
/// <see cref="ISandboxExecutor.ExecuteAsync"/>'s non-nullable return type is a compile-time
/// contract only — a template consumer's own executor implementation, or an unconfigured test
/// double, can still violate it at runtime. Landed as a single call-site fix (#425) and then found
/// duplicated verbatim, comment and all, at two sibling dispatch sites by the same PR's own
/// <c>/code-review</c> pass — the exact "fixed one occurrence of a duplicated pattern and stopped
/// there" shape this repo's CLAUDE.md already tracks. Every call site should go through this
/// extension instead of calling <see cref="ISandboxExecutor.ExecuteAsync"/> directly, so a future
/// dispatch site gets the guarantee by construction rather than by a reviewer remembering to paste
/// the check again.
/// </remarks>
public static class SandboxExecutorExtensions
{
    /// <summary>
    /// Runs <paramref name="executor"/> and throws if it returns null.
    /// </summary>
    /// <param name="executor">The sandbox executor to run.</param>
    /// <param name="request">The execution request to pass through.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The executor's non-null result.</returns>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="executor"/> returned <see langword="null"/> in violation of its own
    /// non-nullable contract. Callers on a dispatch path with an existing catch-all should let this
    /// propagate into it, so it degrades into that path's own stable-error-code failure rather than
    /// an unhandled exception or a confidently "successful" result carrying a null value.
    /// </exception>
    public static async Task<SandboxExecutionResult> ExecuteNonNullAsync(
        this ISandboxExecutor executor, SandboxExecutionRequest request, CancellationToken ct)
    {
        var result = await executor.ExecuteAsync(request, ct).ConfigureAwait(false);

        return result
            ?? throw new InvalidOperationException(
                $"ISandboxExecutor.ExecuteAsync returned null in violation of its non-nullable contract " +
                $"(executor: {executor.GetType().Name}).");
    }
}
