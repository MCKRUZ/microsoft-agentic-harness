using Application.AI.Common.Extensions;
using Application.AI.Common.Interfaces.SkillTraining;
using Application.AI.Common.Services.SkillTraining;
using Domain.AI.SkillTraining;
using FluentAssertions;
using Infrastructure.AI.Evaluation.SkillTraining;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Infrastructure.AI.Evaluation.Tests.SkillTraining;

/// <summary>
/// Pins how the evaluation composition provides <see cref="IRolloutRunner"/> (#843): it replaces the
/// fail-fast default, leaves a runner the consumer registered, and is scoped.
/// </summary>
public sealed class RolloutRunnerRegistrationTests
{
    private static IReadOnlyList<ServiceDescriptor> RunnerRegistrations(IServiceCollection services) =>
        [.. services.Where(d => d.ServiceType == typeof(IRolloutRunner))];

    [Fact]
    public void AddEvaluationDependencies_ReplacesTheFailFastRunner_AsScoped()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IRolloutRunner, NotConfiguredRolloutRunner>();

        services.AddEvaluationDependencies();

        var runner = RunnerRegistrations(services).Should().ContainSingle().Subject;
        runner.ImplementationType.Should().Be<GovernedRolloutRunner>();
        runner.Lifetime.Should().Be(ServiceLifetime.Scoped, "it holds a per-run dataset cache");
    }

    [Fact]
    public void AddEvaluationDependencies_RegisteredBeforeTheApplicationDefault_StillLeavesTheRealRunner()
    {
        var services = new ServiceCollection();

        services.AddEvaluationDependencies();
        services.AddSkillTrainingDependencies();

        RunnerRegistrations(services).Should().ContainSingle().Which.ImplementationType.Should().Be<GovernedRolloutRunner>();
    }

    [Fact]
    public void AddEvaluationDependencies_LeavesARunnerTheConsumerRegisteredThemselves()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IRolloutRunner, ConsumerRunner>();

        services.AddEvaluationDependencies();

        RunnerRegistrations(services).Should().ContainSingle().Which.ImplementationType.Should().Be<ConsumerRunner>();
    }

    private sealed class ConsumerRunner : IRolloutRunner
    {
        public Task<IReadOnlyList<RolloutResult>> RunAsync(string skillContent, RolloutBatch batch, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RolloutResult>>([]);
    }
}
