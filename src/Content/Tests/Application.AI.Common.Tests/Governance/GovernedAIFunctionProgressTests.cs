using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Services.Governance;
using Application.AI.Common.Services.Tools;
using Microsoft.Extensions.AI;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.Governance;

/// <summary>
/// Verifies the governed tool-function wrapper consults the ambient progress evaluator after
/// authorization: it halts on a spin verdict without invoking the inner tool, proceeds on a continue
/// verdict, skips progress evaluation entirely when the governor already denied the call, and passes
/// through when no evaluator is ambient.
/// </summary>
public sealed class GovernedAIFunctionProgressTests : IDisposable
{
    public void Dispose()
    {
        ToolGovernanceAccessor.Current = null;
        ProgressGuardAccessor.Current = null;
    }

    private static (AIFunction inner, Func<bool> wasInvoked) MakeInner()
    {
        var invoked = false;
        var inner = AIFunctionFactory.Create(
            () => { invoked = true; return "inner-result"; },
            new AIFunctionFactoryOptions { Name = "file_system", Description = "test tool" });
        return (inner, () => invoked);
    }

    [Fact]
    public async Task InvokeAsync_ProgressHalts_ReturnsHaltMessageAndSkipsInner()
    {
        var (inner, wasInvoked) = MakeInner();
        var progress = new Mock<IProgressEvaluator>();
        progress
            .Setup(p => p.Evaluate(It.IsAny<string>(), It.IsAny<Func<string?>>()))
            .Returns(ProgressVerdict.Halt("Error: tool 'file_system' was stopped — repeating without progress."));
        ProgressGuardAccessor.Current = progress.Object;

        var governed = new GovernedAIFunction(inner);
        var result = await governed.InvokeAsync(new AIFunctionArguments(), CancellationToken.None);

        Assert.False(wasInvoked(), "inner tool must not run when the progress guard halts");
        Assert.Contains("repeating without progress", result?.ToString());
    }

    [Fact]
    public async Task InvokeAsync_ProgressContinues_InvokesInner()
    {
        var (inner, wasInvoked) = MakeInner();
        var progress = new Mock<IProgressEvaluator>();
        progress
            .Setup(p => p.Evaluate(It.IsAny<string>(), It.IsAny<Func<string?>>()))
            .Returns(ProgressVerdict.Continue());
        ProgressGuardAccessor.Current = progress.Object;

        var governed = new GovernedAIFunction(inner);
        await governed.InvokeAsync(new AIFunctionArguments(), CancellationToken.None);

        Assert.True(wasInvoked(), "inner tool must run when the progress guard allows");
    }

    [Fact]
    public async Task InvokeAsync_NoAmbientEvaluator_PassesThrough()
    {
        var (inner, wasInvoked) = MakeInner();
        ProgressGuardAccessor.Current = null;

        var governed = new GovernedAIFunction(inner);
        await governed.InvokeAsync(new AIFunctionArguments(), CancellationToken.None);

        Assert.True(wasInvoked(), "inner tool must run when no progress evaluator is ambient");
    }

    [Fact]
    public async Task InvokeAsync_GovernorDenies_ProgressNotConsulted()
    {
        var (inner, _) = MakeInner();
        var governor = new Mock<IToolInvocationGovernor>();
        governor
            .Setup(g => g.AuthorizeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ToolInvocationDecision.Deny("Error: tool 'file_system' is not permitted."));
        ToolGovernanceAccessor.Current = governor.Object;

        var progress = new Mock<IProgressEvaluator>(MockBehavior.Strict);
        ProgressGuardAccessor.Current = progress.Object;

        var governed = new GovernedAIFunction(inner);
        await governed.InvokeAsync(new AIFunctionArguments(), CancellationToken.None);

        // A denied call never executed, so it must not count toward progress: Evaluate is never called.
        progress.Verify(p => p.Evaluate(It.IsAny<string>(), It.IsAny<Func<string?>>()), Times.Never);
    }

    [Fact]
    public async Task InvokeAsync_PassesToolNameToEvaluator()
    {
        var (inner, _) = MakeInner();
        var progress = new Mock<IProgressEvaluator>();
        progress
            .Setup(p => p.Evaluate(It.IsAny<string>(), It.IsAny<Func<string?>>()))
            .Returns(ProgressVerdict.Continue());
        ProgressGuardAccessor.Current = progress.Object;

        var governed = new GovernedAIFunction(inner);
        await governed.InvokeAsync(new AIFunctionArguments(), CancellationToken.None);

        progress.Verify(p => p.Evaluate("file_system", It.IsAny<Func<string?>>()), Times.Once);
    }
}
