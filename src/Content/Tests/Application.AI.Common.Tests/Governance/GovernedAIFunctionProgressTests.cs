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
        ToolCallObserverAccessor.Current = null;
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
            .Setup(g => g.AuthorizeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyDictionary<string, object?>?>()))
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

    [Fact]
    public async Task InvokeAsync_ObserverBlocks_ProgressNotConsulted()
    {
        // The progress evaluator is the only stage on this path that RECORDS state: it remembers the
        // call's signature and resets the no-progress counter whenever it sees a new one. Counting a
        // call an observer blocked is not a bookkeeping nicety — it defeats the guard outright. An
        // agent retrying a blocked call with one value changed each time (10000, 10001, 10002 …)
        // presents a brand-new signature on every attempt, so every attempt would reset the counter
        // and the spin against the observer's own rule would run to the iteration ceiling.
        var (inner, wasInvoked) = MakeInner();

        var observers = new Mock<IToolCallObserverChain>();
        observers.SetupGet(o => o.HasObservers).Returns(true);
        observers
            .Setup(o => o.EvaluateAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyDictionary<string, object?>>(),
                It.IsAny<CancellationToken>()))
            .Returns(ValueTask.FromResult(ToolInvocationDecision.Deny("Error: tool 'file_system' is not permitted.")));
        ToolCallObserverAccessor.Current = observers.Object;

        // Strict: any call to Evaluate at all is the defect, not just a particular verdict.
        var progress = new Mock<IProgressEvaluator>(MockBehavior.Strict);
        ProgressGuardAccessor.Current = progress.Object;

        var governed = new GovernedAIFunction(inner);
        await governed.InvokeAsync(new AIFunctionArguments(), CancellationToken.None);

        Assert.False(wasInvoked(), "inner tool must not run when an observer blocks");
        progress.Verify(p => p.Evaluate(It.IsAny<string>(), It.IsAny<Func<string?>>()), Times.Never);
    }

    [Fact]
    public async Task InvokeAsync_ObserverAllows_ProgressStillCountsTheCall()
    {
        // The mirror of the test above: moving the guard behind the observers must not stop it
        // counting the calls that do execute, or the spin detector would never fire at all.
        var (inner, wasInvoked) = MakeInner();

        var observers = new Mock<IToolCallObserverChain>();
        observers.SetupGet(o => o.HasObservers).Returns(true);
        observers
            .Setup(o => o.EvaluateAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyDictionary<string, object?>>(),
                It.IsAny<CancellationToken>()))
            .Returns(ValueTask.FromResult(ToolInvocationDecision.Allow()));
        ToolCallObserverAccessor.Current = observers.Object;

        var progress = new Mock<IProgressEvaluator>();
        progress
            .Setup(p => p.Evaluate(It.IsAny<string>(), It.IsAny<Func<string?>>()))
            .Returns(ProgressVerdict.Continue());
        ProgressGuardAccessor.Current = progress.Object;

        var governed = new GovernedAIFunction(inner);
        await governed.InvokeAsync(new AIFunctionArguments(), CancellationToken.None);

        Assert.True(wasInvoked());
        progress.Verify(p => p.Evaluate("file_system", It.IsAny<Func<string?>>()), Times.Once);
    }
}
