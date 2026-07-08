using Application.AI.Common.Interfaces.Sandbox;
using Domain.AI.Sandbox;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure.AI.Tests.Support;

/// <summary>
/// Builds minimal <see cref="IServiceScopeFactory"/> instances around test
/// doubles, for exercising services that resolve scope-bound collaborators
/// (<see cref="IMediator"/>, the keyed-scoped <see cref="ISandboxExecutor"/>)
/// from a fresh scope per operation instead of capturing them at construction.
/// </summary>
internal static class TestScopeFactory
{
    /// <summary>Scope factory whose scopes resolve <see cref="IMediator"/> to <paramref name="mediator"/>.</summary>
    public static IServiceScopeFactory For(IMediator mediator) =>
        new ServiceCollection()
            .AddScoped(_ => mediator)
            .BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();

    /// <summary>
    /// Scope factory whose scopes resolve the keyed <see cref="ISandboxExecutor"/> for
    /// <paramref name="isolationLevel"/> to <paramref name="sandbox"/>. Mirrors the
    /// production keyed-SCOPED registration in <c>DependencyInjection.Planner.cs</c>.
    /// </summary>
    public static IServiceScopeFactory ForSandbox(
        ISandboxExecutor sandbox,
        SandboxIsolationLevel isolationLevel = SandboxIsolationLevel.Process) =>
        new ServiceCollection()
            .AddKeyedScoped(isolationLevel, (_, _) => sandbox)
            .BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();
}
