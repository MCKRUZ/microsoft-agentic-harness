using Domain.Common.Config;
using Infrastructure.AI.Evaluation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Presentation.Common.Extensions;

namespace Presentation.EvalRunner.Tests.Composition;

/// <summary>
/// Builds the same service graph the real EvalRunner host composes (<c>Program.cs</c>: the shared
/// solution root, then <see cref="DependencyInjection.AddEvaluationDependencies"/>), against an
/// empty in-memory configuration.
/// </summary>
/// <remarks>
/// Shared by every test in this folder that needs the real eval-framework service graph — this
/// exact sequence used to be duplicated per test file (a /simplify finding).
/// </remarks>
internal static class EvalRunnerTestComposition
{
    /// <summary>Builds an unregistered <see cref="ServiceCollection"/> mirroring the EvalRunner host.</summary>
    public static ServiceCollection BuildServices()
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
}
