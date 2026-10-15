using Application.Core.CQRS.Evaluation.RunEvalSuite;
using Domain.AI.Evaluation;
using Domain.Common;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Tests.Common;
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
        await using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        var command = new RunEvalSuiteCommand { DatasetPaths = ["D:/definitely/not/a/dataset.yaml"] };

        Result<EvalRunReport>? result = null;
        var outcome = await Record.ExceptionAsync(async () =>
            result = await EvalSuiteDispatcher.DispatchAsync(provider, command, CancellationToken.None));

        // A scope violation is an InvalidOperationException raised while the pipeline is still being
        // resolved; a missing dataset is a refusal the pipeline or handler hands back as a failed Result.
        outcome.Should().BeNull("a missing dataset is a failed Result, never a container error");
        result.Should().NotBeNull();
        result!.IsSuccess.Should().BeFalse("the dataset does not exist, so the handler or validator must refuse it");
    }

    [Fact]
    public void Program_DispatchesThroughTheScopedDispatcher_NeverResolvingTheMediatorFromTheRoot()
    {
        // The test above proves the dispatcher is scope-safe; this proves Program.cs still goes through it.
        // Resolving IMediator from the root provider again is the exact line that restores #834.
        var source = File.ReadAllText(RepoRoot.Combine(
            "src", "Content", "Presentation", "Presentation.EvalRunner", "Program.cs"));

        source.Should().Contain("EvalSuiteDispatcher.DispatchAsync(");
        source.Should().NotContain("GetRequiredService<IMediator>");
    }
}
