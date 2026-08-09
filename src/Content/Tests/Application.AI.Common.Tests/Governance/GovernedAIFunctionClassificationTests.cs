using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Services.Governance;
using Application.AI.Common.Services.Tools;
using Microsoft.Extensions.AI;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.Governance;

/// <summary>
/// Verifies data classification as the agent turn actually reaches it: through the real admission
/// chain, armed ambiently, from the governed tool-function wrapper. A block returns the gate's message
/// without running the tool, a redact verdict runs the tool and scrubs its output, an allow runs the
/// tool unchanged, and an unarmed turn passes through.
/// </summary>
/// <remarks>
/// The redact case is the reason admission is not purely a pre-call decision: the verdict has to
/// survive the tool call and be applied to its output. That second half lives on the chain
/// (<see cref="IToolCallAdmissionPipeline.ApplyOutputPolicy"/>) rather than at each caller, so a caller
/// can neither hold the gate itself nor forget to consult it.
/// </remarks>
public sealed class GovernedAIFunctionClassificationTests
{
    private static (AIFunction inner, Func<bool> wasInvoked) MakeInner()
    {
        var invoked = false;
        var inner = AIFunctionFactory.Create(
            () => { invoked = true; return "inner-result"; },
            new AIFunctionFactoryOptions { Name = "file_system", Description = "test tool" });
        return (inner, () => invoked);
    }

    private static Mock<IToolClassificationGate> GateReturning(ClassificationVerdict verdict)
    {
        var gate = new Mock<IToolClassificationGate>();
        gate
            .Setup(g => g.EvaluateAsync(
                It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(verdict);
        return gate;
    }

    private static async Task<object?> InvokeUnder(IToolCallAdmissionPipeline pipeline, AIFunction inner)
    {
        using var armed = ToolAdmissionAccessor.Begin(pipeline);
        return await new GovernedAIFunction(inner).InvokeAsync(new AIFunctionArguments(), CancellationToken.None);
    }

    [Fact]
    public async Task InvokeAsync_ClassificationBlocks_ReturnsMessageAndSkipsInner()
    {
        var (inner, wasInvoked) = MakeInner();
        var gate = GateReturning(ClassificationVerdict.Block("Error: tool 'file_system' is not permitted: restricted data."));

        var result = await InvokeUnder(AdmissionHarness.Pipeline(classificationGate: gate.Object), inner);

        Assert.False(wasInvoked(), "inner tool must not run when classification blocks");
        Assert.Contains("not permitted", result?.ToString());
    }

    [Fact]
    public async Task InvokeAsync_ClassificationRedacts_RunsInnerThenScrubsOutput()
    {
        var (inner, wasInvoked) = MakeInner();
        var gate = GateReturning(ClassificationVerdict.RedactOutput());
        gate.Setup(g => g.RedactResult("file_system", It.IsAny<object?>())).Returns("[redacted]");

        var result = await InvokeUnder(AdmissionHarness.Pipeline(classificationGate: gate.Object), inner);

        Assert.True(wasInvoked(), "a redact verdict still runs the tool");
        Assert.Equal("[redacted]", result?.ToString());
        // The tool result reaches the gate as the pipeline's serialized form (a JsonElement), not a bare
        // string, so the redactor is verified on the call rather than the exact argument type.
        gate.Verify(g => g.RedactResult("file_system", It.IsAny<object?>()), Times.Once);
    }

    [Fact]
    public async Task InvokeAsync_ClassificationAllows_RunsInnerUnchanged()
    {
        var (inner, wasInvoked) = MakeInner();
        var gate = GateReturning(ClassificationVerdict.Allow());

        var result = await InvokeUnder(AdmissionHarness.Pipeline(classificationGate: gate.Object), inner);

        Assert.True(wasInvoked());
        Assert.Equal("inner-result", result?.ToString());
        gate.Verify(g => g.RedactResult(It.IsAny<string>(), It.IsAny<object?>()), Times.Never);
    }

    [Fact]
    public async Task InvokeAsync_ClassificationBlocks_SkipsProgressGuard()
    {
        // Ordering guarantee: a classification block returns before the progress guard, so a blocked call
        // (which never executes) must not be counted toward progress.
        var (inner, _) = MakeInner();
        var gate = GateReturning(ClassificationVerdict.Block("blocked"));
        var progress = new Mock<IProgressEvaluator>();

        await InvokeUnder(
            AdmissionHarness.Pipeline(classificationGate: gate.Object, progressEvaluator: progress.Object), inner);

        progress.Verify(p => p.Evaluate(It.IsAny<string>(), It.IsAny<Func<string?>>()), Times.Never,
            "a classification-blocked call must not reach the progress guard — asking it is also what "
            + "counts the call, and a blocked call never executed");
    }

    [Fact]
    public async Task InvokeAsync_NoAmbientChain_PassesThrough()
    {
        var (inner, wasInvoked) = MakeInner();

        var result = await new GovernedAIFunction(inner)
            .InvokeAsync(new AIFunctionArguments(), CancellationToken.None);

        Assert.True(wasInvoked());
        Assert.Equal("inner-result", result?.ToString());
    }
}
