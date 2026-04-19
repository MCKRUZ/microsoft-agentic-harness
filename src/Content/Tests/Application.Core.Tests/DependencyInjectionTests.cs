using Application.Core.CQRS.Agents.ExecuteAgentTurn;
using Application.Core.CQRS.Agents.RunConversation;
using Application.Core.CQRS.Agents.RunOrchestratedTask;
using Application.Core.CQRS.MetaHarness;
using FluentAssertions;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Application.Core.Tests;

/// <summary>
/// Tests for <see cref="DependencyInjection.AddApplicationCoreDependencies"/>,
/// verifying that FluentValidation validators are registered via assembly scanning.
/// </summary>
public class DependencyInjectionTests
{
    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddApplicationCoreDependencies();
        return services.BuildServiceProvider();
    }

    [Fact]
    public void AddApplicationCoreDependencies_RegistersExecuteAgentTurnCommandValidator()
    {
        using var provider = BuildProvider();

        var validators = provider.GetServices<IValidator<ExecuteAgentTurnCommand>>();

        validators.Should().ContainSingle()
            .Which.Should().BeOfType<ExecuteAgentTurnCommandValidator>();
    }

    [Fact]
    public void AddApplicationCoreDependencies_RegistersRunConversationCommandValidator()
    {
        using var provider = BuildProvider();

        var validators = provider.GetServices<IValidator<RunConversationCommand>>();

        validators.Should().ContainSingle()
            .Which.Should().BeOfType<RunConversationCommandValidator>();
    }

    [Fact]
    public void AddApplicationCoreDependencies_RegistersRunOrchestratedTaskCommandValidator()
    {
        using var provider = BuildProvider();

        var validators = provider.GetServices<IValidator<RunOrchestratedTaskCommand>>();

        validators.Should().ContainSingle()
            .Which.Should().BeOfType<RunOrchestratedTaskCommandValidator>();
    }

    [Fact]
    public void AddApplicationCoreDependencies_RegistersRunHarnessOptimizationCommandValidator()
    {
        using var provider = BuildProvider();

        var validators = provider.GetServices<IValidator<RunHarnessOptimizationCommand>>();

        validators.Should().ContainSingle()
            .Which.Should().BeOfType<RunHarnessOptimizationCommandValidator>();
    }

    [Fact]
    public void AddApplicationCoreDependencies_ReturnsServiceCollection()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        var result = services.AddApplicationCoreDependencies();

        result.Should().BeSameAs(services);
    }
}
