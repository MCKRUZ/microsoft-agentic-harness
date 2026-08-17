using Application.AI.Common.Interfaces.Sandbox;
using Application.AI.Common.Services.Sandbox;
using Application.AI.Common.Services.Tools;
using Domain.AI.Sandbox;
using Domain.Common.Config.AI.Sandbox;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

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
    /// <paramref name="isolationLevel"/> to <paramref name="sandbox"/>, plus a real
    /// <see cref="ToolPermissionProfileResolver"/> over an empty tool set and default
    /// <see cref="SandboxConfig"/> (no overrides) — the operator-override merge
    /// <c>WorkspaceCommandRunner</c>/<c>IacSandboxRunner</c> now resolve per call (#405) needs one
    /// present in scope even when the test isn't exercising an override itself. Mirrors the
    /// production keyed-SCOPED registration in <c>DependencyInjection.Planner.cs</c>.
    /// </summary>
    public static IServiceScopeFactory ForSandbox(
        ISandboxExecutor sandbox,
        SandboxIsolationLevel isolationLevel = SandboxIsolationLevel.Process) =>
        ForSandbox(sandbox, new SandboxConfig(), isolationLevel);

    /// <summary>
    /// As <see cref="ForSandbox(ISandboxExecutor, SandboxIsolationLevel)"/>, but with an explicit
    /// <paramref name="sandboxConfig"/> so a test can prove an operator's per-tool override
    /// (<c>DeniedCapabilities</c>/<c>MinimumIsolation</c>) actually reaches a caller that resolves
    /// through the returned scope (#405).
    /// </summary>
    public static IServiceScopeFactory ForSandbox(
        ISandboxExecutor sandbox,
        SandboxConfig sandboxConfig,
        SandboxIsolationLevel isolationLevel = SandboxIsolationLevel.Process)
    {
        var services = new ServiceCollection()
            .AddKeyedScoped(isolationLevel, (_, _) => sandbox);
        services.AddOptions<SandboxConfig>().Configure(c =>
        {
            foreach (var (name, overrideConfig) in sandboxConfig.ToolOverrides)
                c.ToolOverrides[name] = overrideConfig;
        });
        services.AddSingleton(sp => new FirstPartyToolLookup(sp, new HashSet<string>()));
        services.AddSingleton(sp => new ToolPermissionProfileResolver(
            sp.GetRequiredService<FirstPartyToolLookup>(),
            sp.GetRequiredService<IOptionsMonitor<SandboxConfig>>()));

        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }
}
