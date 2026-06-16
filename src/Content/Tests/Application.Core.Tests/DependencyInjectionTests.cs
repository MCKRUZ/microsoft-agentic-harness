using Application.AI.Common.Interfaces.Escalation;
using Application.AI.Common.Interfaces.MetaHarness;
using Application.Core.CQRS.Agents.ExecuteAgentTurn;
using Application.Core.CQRS.Agents.RunConversation;
using Application.Core.CQRS.Agents.RunOrchestratedTask;
using Application.Core.CQRS.MetaHarness;
using Application.Core.Escalation.Strategies;
using Domain.AI.Escalation;
using FluentAssertions;
using FluentValidation;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.DependencyInjection;
using Moq;
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

    [Theory]
    [InlineData(ApprovalStrategyType.AnyOf, typeof(AnyOfApprovalStrategy))]
    [InlineData(ApprovalStrategyType.AllOf, typeof(AllOfApprovalStrategy))]
    [InlineData(ApprovalStrategyType.Quorum, typeof(QuorumApprovalStrategy))]
    public void AddApplicationCoreDependencies_RegistersApprovalStrategies_KeyedByType(
        ApprovalStrategyType key, Type expectedType)
    {
        using var provider = BuildProvider();

        var strategy = provider.GetKeyedService<IApprovalStrategy>(key);

        strategy.Should().NotBeNull().And.BeOfType(expectedType);
    }

    /// <summary>
    /// Registers mock meta-harness dependencies with their production lifetimes:
    /// <see cref="IHarnessProposer"/> and <see cref="IEvaluationService"/> are SCOPED
    /// (as in Infrastructure.AI), <see cref="IHarnessCandidateRepository"/> is SINGLETON.
    /// </summary>
    private static ServiceProvider BuildProviderWithMetaHarnessDeps(bool validateScopes)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped(_ => Mock.Of<IHarnessProposer>());
        services.AddScoped(_ => Mock.Of<IEvaluationService>());
        services.AddSingleton(_ => Mock.Of<IHarnessCandidateRepository>());
        services.AddApplicationCoreDependencies();

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = validateScopes,
        });
    }

    [Fact]
    public void AddWorkflowDependencies_OptimizationIteration_ResolvesFromScopeUnderScopeValidation()
    {
        // Regression: when this workflow was a keyed SINGLETON, its factory received the
        // root provider and resolving the SCOPED IHarnessProposer / IEvaluationService threw
        // "Cannot resolve scoped service ... from root provider" under scope validation.
        using var provider = BuildProviderWithMetaHarnessDeps(validateScopes: true);
        using var scope = provider.CreateScope();

        var workflow = scope.ServiceProvider
            .GetRequiredKeyedService<Workflow>("optimization-iteration");

        workflow.Should().NotBeNull();
    }

    [Fact]
    public void AddWorkflowDependencies_OptimizationIteration_ResolvingFromRootThrowsUnderScopeValidation()
    {
        // The key is scoped, so resolving it directly from the ROOT provider (no scope) must
        // be rejected by scope validation — proving the registration is genuinely scoped and
        // consumers are steered to resolve it from a created scope.
        using var provider = BuildProviderWithMetaHarnessDeps(validateScopes: true);

        var act = () => provider.GetRequiredKeyedService<Workflow>("optimization-iteration");

        act.Should().Throw<InvalidOperationException>();
    }
}
