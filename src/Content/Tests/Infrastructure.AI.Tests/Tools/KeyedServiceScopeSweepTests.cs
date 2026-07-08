using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Iac;
using Application.AI.Common.Interfaces.Sandbox;
using Application.AI.Common.Interfaces.Tools;
using Domain.AI.Sandbox;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.GitOps;
using FluentAssertions;
using FluentAssertions.Execution;
using Infrastructure.AI.Tests.Iac;
using Infrastructure.AI.Tools.GitOps;
using Infrastructure.AI.Tools.Iac;
using Infrastructure.AI.Tools.Workspace;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Tools;

/// <summary>
/// Captive-dependency SWEEP: binds every public skill-pack DI extension
/// (workspace, GitOps, IaC), then resolves EVERY keyed <see cref="ITool"/> and
/// every keyed <see cref="IIacGenerator"/> from a created scope under
/// <c>ValidateScopes = true</c>. Any keyed-singleton factory that resolves a
/// scope-bound service (the keyed-SCOPED <see cref="ISandboxExecutor"/>, a
/// root-bound <see cref="IMediator"/>) from the root provider fails here —
/// so new tools cannot reintroduce the captive-dependency class of bug that
/// the per-service regression tests only catch for services they enumerate.
/// </summary>
/// <remarks>
/// Coverage boundary: tools registered by the private
/// <c>DependencyInjection.RegisterToolServices</c> (file_system, document_ingest,
/// echo, …) are not swept — that method is not callable in isolation. Their
/// mediator-shaped member (<c>document_ingest</c>) is covered by its own
/// scope-per-dispatch design and the sibling regression tests.
/// </remarks>
public sealed class KeyedServiceScopeSweepTests
{
    private static ServiceCollection BuildProductionComposition()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpClient();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(SweepConfigMonitor());
        services.AddScoped(_ => Mock.Of<IMediator>());
        services.AddSingleton(Mock.Of<IMcpToolProvider>());

        // Production lifetime (DependencyInjection.Planner.cs): keyed SCOPED executors.
        services.AddKeyedScoped<ISandboxExecutor>(
            SandboxIsolationLevel.Process, (_, _) => Mock.Of<ISandboxExecutor>());
        services.AddKeyedScoped<ISandboxExecutor>(
            SandboxIsolationLevel.Container, (_, _) => Mock.Of<ISandboxExecutor>());

        // The public skill-pack extensions, exactly as the production composition
        // root chains them.
        services.AddWorkspaceSkillTools();
        services.AddGitOpsSkillTools();
        services.AddIacSkillTools();

        return services;
    }

    private static IOptionsMonitor<AppConfig> SweepConfigMonitor()
    {
        // Valid IaC section + an active GitOps controller so the default
        // IGitOpsController registration can resolve the keyed "flux" controller.
        var config = IacTestConfig.ValidAppConfig();
        config.AI.GitOps = new GitOpsConfig
        {
            Enabled = true,
            ActiveController = "flux",
        };
        return IacTestConfig.Monitor(config);
    }

    [Fact]
    public void EveryKeyedTool_ResolvesFromScope_UnderScopeValidation()
    {
        var services = BuildProductionComposition();

        var toolKeys = services
            .Where(d => d.IsKeyedService && d.ServiceType == typeof(ITool) && d.ServiceKey is not null)
            .Select(d => d.ServiceKey!)
            .Distinct()
            .ToList();

        toolKeys.Should().NotBeEmpty("the sweep is pointless if no keyed tools were registered");

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
        });
        using var scope = provider.CreateScope();

        using (new AssertionScope())
        {
            foreach (var key in toolKeys)
            {
                var act = () => scope.ServiceProvider.GetRequiredKeyedService<ITool>(key);
                act.Should().NotThrow(
                    "keyed tool '{0}' must not resolve scope-bound services from the root provider at construction", key);
            }
        }
    }

    [Fact]
    public void EveryKeyedIacGenerator_ResolvesFromScope_UnderScopeValidation()
    {
        var services = BuildProductionComposition();

        var generatorKeys = services
            .Where(d => d.IsKeyedService && d.ServiceType == typeof(IIacGenerator) && d.ServiceKey is not null)
            .Select(d => d.ServiceKey!)
            .Distinct()
            .ToList();

        generatorKeys.Should().NotBeEmpty("AddIacSkillTools registers both backend generators");

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
        });
        using var scope = provider.CreateScope();

        using (new AssertionScope())
        {
            foreach (var key in generatorKeys)
            {
                var act = () => scope.ServiceProvider.GetRequiredKeyedService<IIacGenerator>(key);
                act.Should().NotThrow(
                    "keyed IaC generator '{0}' must not resolve scope-bound services from the root provider at construction", key);
            }
        }
    }
}
