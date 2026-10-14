using Application.Core.CQRS.Evaluation.RunEvalSuite;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Presentation.EvalRunner.Tests.Composition;

/// <summary>
/// Pins that the EvalRunner can actually dispatch its command (#834). The host builds its container with
/// scope validation on, and the MediatR validation behaviour needs the scoped validators, so a dispatch made
/// through the root provider is refused before any handler runs. The composition tests next door never send
/// the command, which is how the EvalRunner stayed unable to run.
/// </summary>
public sealed class EvalSuiteDispatcherTests
{
    [Fact]
    public async Task DispatchAsync_ThroughAScopeValidatedContainer_ReachesTheHandlerInsteadOfFailingOnScope()
    {
        var services = EvalRunnerTestComposition.BuildServices();
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var command = new RunEvalSuiteCommand { DatasetPaths = ["D:/definitely/not/a/dataset.yaml"] };

        var outcome = await Record.ExceptionAsync(
            () => EvalSuiteDispatcher.DispatchAsync(provider, command, CancellationToken.None));

        // Whatever the pipeline decides about this missing dataset, it must DECIDE: a scope violation is
        // an InvalidOperationException raised while the pipeline is still being resolved.
        outcome.Should().BeNull("a missing dataset is a failed Result, never a container error");
    }
}
