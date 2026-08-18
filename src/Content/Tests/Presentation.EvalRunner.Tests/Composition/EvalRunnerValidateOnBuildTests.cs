using Application.AI.Common.Evaluation.Interfaces;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Presentation.EvalRunner.Tests.Composition;

/// <summary>
/// Guards the EvalRunner host's composition for audit item H2. The EvalRunner is the one host
/// that opts into the evaluation framework, so it exercises two things the shared-root sweep
/// test cannot: the eval-specific service graph under <c>ValidateOnBuild</c>, and the
/// last-registration-wins override that must replace the composition root's fail-fast
/// <c>NotConfiguredEvalRunner</c> default with the real
/// <see cref="Infrastructure.AI.Evaluation.Runners.EvalRunner"/>.
/// </summary>
public sealed class EvalRunnerValidateOnBuildTests
{
    [Fact]
    public void EvalRunnerComposition_BuildsWithValidateOnBuild()
    {
        var services = EvalRunnerTestComposition.BuildServices();

        var exception = Record.Exception(() =>
        {
            using var provider = services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true,
            });
        });

        exception.Should().BeNull();
    }

    [Fact]
    public void EvalRunnerComposition_ResolvesRealEvalRunner_NotTheNotConfiguredDefault()
    {
        var services = EvalRunnerTestComposition.BuildServices();

        // No validation flags needed here — this asserts resolution only; the sibling
        // BuildsWithValidateOnBuild test pins the validation policy for this same graph.
        using var provider = services.BuildServiceProvider();

        // The composition root registers NotConfiguredEvalRunner so every host can construct
        // RunEvalSuiteCommandHandler; AddEvaluationDependencies (called after) must win here.
        provider.GetRequiredService<IEvalRunner>()
            .Should().BeOfType<Infrastructure.AI.Evaluation.Runners.EvalRunner>();
    }
}
