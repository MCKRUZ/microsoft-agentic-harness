using Application.AI.Common.Evaluation.Interfaces;
using Domain.Common.Config;
using FluentAssertions;
using Infrastructure.AI.Evaluation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Presentation.Common.Extensions;
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
    private static ServiceCollection BuildEvalRunnerServices()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.RegisterConfigSections(configuration);
        var appConfig = configuration.GetSection("AppConfig").Get<AppConfig>() ?? new AppConfig();
        services.BuildGlobalSolutionServices(appConfig, includeHealthChecksUI: false);

        // Mirror Program.cs: the eval host opts into the framework AFTER the shared root.
        services.AddEvaluationDependencies();
        return services;
    }

    [Fact]
    public void EvalRunnerComposition_BuildsWithValidateOnBuild()
    {
        var services = BuildEvalRunnerServices();

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
        var services = BuildEvalRunnerServices();

        // No validation flags needed here — this asserts resolution only; the sibling
        // BuildsWithValidateOnBuild test pins the validation policy for this same graph.
        using var provider = services.BuildServiceProvider();

        // The composition root registers NotConfiguredEvalRunner so every host can construct
        // RunEvalSuiteCommandHandler; AddEvaluationDependencies (called after) must win here.
        provider.GetRequiredService<IEvalRunner>()
            .Should().BeOfType<Infrastructure.AI.Evaluation.Runners.EvalRunner>();
    }
}
