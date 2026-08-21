using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Telemetry;
using Application.AI.Common.Services.Governance;
using Application.AI.Common.Services.Tools;
using Infrastructure.AI.Telemetry.Redaction;
using Microsoft.Extensions.AI;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.Governance;

/// <summary>
/// Verifies the loop guard as the agent turn actually reaches it: through the real admission chain,
/// armed ambiently, from the governed tool-function wrapper.
/// </summary>
/// <remarks>
/// <para>
/// The chain is real in every test here, not a mock. The agent turn is the only caller that asks for
/// loop detection, so "does the guard run, and only for calls that reached the tool" is a property of
/// the wrapper and the chain <em>together</em> — a mocked chain would move the assertion off the thing
/// that can break.
/// </para>
/// <para>
/// No teardown is needed: <see cref="ToolAdmissionAccessor.Begin"/> restores the previous ambient value
/// on dispose, so a test cannot leak its chain into the next one.
/// </para>
/// </remarks>
public sealed class GovernedAIFunctionProgressTests
{
    private static readonly IContentRedactionFilter RedactionFilter = TestRedactionFilter.Instance;

    private static (AIFunction inner, Func<bool> wasInvoked) MakeInner()
    {
        var invoked = false;
        var inner = AIFunctionFactory.Create(
            () => { invoked = true; return "inner-result"; },
            new AIFunctionFactoryOptions { Name = "file_system", Description = "test tool" });
        return (inner, () => invoked);
    }

    private static async Task<object?> InvokeUnder(IToolCallAdmissionPipeline pipeline, AIFunction inner)
    {
        using var armed = ToolAdmissionAccessor.Begin(pipeline);
        return await new GovernedAIFunction(inner, RedactionFilter).InvokeAsync(new AIFunctionArguments(), CancellationToken.None);
    }

    [Fact]
    public async Task InvokeAsync_ProgressHalts_ReturnsHaltMessageAndSkipsInner()
    {
        var (inner, wasInvoked) = MakeInner();
        var progress = new Mock<IProgressEvaluator>();
        progress
            .Setup(p => p.Evaluate(It.IsAny<string>(), It.IsAny<Func<string?>>()))
            .Returns(ProgressVerdict.Halt("Error: tool 'file_system' was stopped — repeating without progress."));

        var result = await InvokeUnder(AdmissionHarness.Pipeline(progressEvaluator: progress.Object), inner);

        Assert.False(wasInvoked(), "inner tool must not run when the progress guard halts");
        Assert.Contains("repeating without progress", result?.ToString());
    }

    [Fact]
    public async Task InvokeAsync_ProgressContinues_InvokesInner()
    {
        var (inner, wasInvoked) = MakeInner();

        await InvokeUnder(AdmissionHarness.Pipeline(), inner);

        Assert.True(wasInvoked(), "inner tool must run when the progress guard allows");
    }

    [Fact]
    public async Task InvokeAsync_NoAmbientChain_PassesThrough()
    {
        // A tool invoked outside a governed turn — nothing is armed, so the wrapper is transparent.
        var (inner, wasInvoked) = MakeInner();

        await new GovernedAIFunction(inner, RedactionFilter).InvokeAsync(new AIFunctionArguments(), CancellationToken.None);

        Assert.True(wasInvoked(), "inner tool must run when no admission chain is ambient");
    }

    [Fact]
    public async Task InvokeAsync_GovernorDenies_ProgressNotConsulted()
    {
        var (inner, _) = MakeInner();
        var governor = new Mock<IToolInvocationGovernor>();
        governor
            .Setup(g => g.AuthorizeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyDictionary<string, object?>?>()))
            .ReturnsAsync(ToolInvocationDecision.Deny("Error: tool 'file_system' is not permitted."));

        // Strict: any call to Evaluate at all is the defect, not just a particular verdict — asking
        // the guard is also what records the call.
        var progress = new Mock<IProgressEvaluator>(MockBehavior.Strict);

        await InvokeUnder(
            AdmissionHarness.Pipeline(governor: governor.Object, progressEvaluator: progress.Object), inner);

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

        await InvokeUnder(AdmissionHarness.Pipeline(progressEvaluator: progress.Object), inner);

        progress.Verify(p => p.Evaluate("file_system", It.IsAny<Func<string?>>()), Times.Once);
    }

    [Fact]
    public async Task InvokeAsync_ObserverBlocks_ProgressNotConsulted()
    {
        // The progress evaluator is the only stage on this path that RECORDS state: asking it about a
        // call is also what remembers that call. Counting a call an observer blocked is not a
        // bookkeeping nicety — it defeats the guard outright. An agent retrying a blocked call with one
        // value changed each time (10000, 10001, 10002 …) presents a brand-new signature on every
        // attempt, so every attempt would reset the counter and the spin against the observer's own
        // rule would run to the iteration ceiling.
        var (inner, wasInvoked) = MakeInner();
        var observers = AdmissionHarness.ObserverChain(
            ToolInvocationDecision.Deny("Error: tool 'file_system' is not permitted."));

        // Strict: any call to Evaluate at all is the defect, not just a particular verdict.
        var progress = new Mock<IProgressEvaluator>(MockBehavior.Strict);

        await InvokeUnder(
            AdmissionHarness.Pipeline(observers: observers.Object, progressEvaluator: progress.Object), inner);

        Assert.False(wasInvoked(), "inner tool must not run when an observer blocks");
        progress.Verify(p => p.Evaluate(It.IsAny<string>(), It.IsAny<Func<string?>>()), Times.Never);
    }

    [Fact]
    public async Task InvokeAsync_ObserverAllows_ProgressStillCountsTheCall()
    {
        // The mirror of the test above: keeping the guard behind the observers must not stop it
        // counting the calls that do execute, or the spin detector would never fire at all.
        var (inner, wasInvoked) = MakeInner();
        var observers = AdmissionHarness.ObserverChain(ToolInvocationDecision.Allow());

        var progress = new Mock<IProgressEvaluator>();
        progress
            .Setup(p => p.Evaluate(It.IsAny<string>(), It.IsAny<Func<string?>>()))
            .Returns(ProgressVerdict.Continue());

        await InvokeUnder(
            AdmissionHarness.Pipeline(observers: observers.Object, progressEvaluator: progress.Object), inner);

        Assert.True(wasInvoked());
        progress.Verify(p => p.Evaluate("file_system", It.IsAny<Func<string?>>()), Times.Once);
    }
}
