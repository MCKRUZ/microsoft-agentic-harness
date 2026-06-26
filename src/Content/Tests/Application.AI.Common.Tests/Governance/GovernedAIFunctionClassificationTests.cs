using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Services.Governance;
using Application.AI.Common.Services.Tools;
using Microsoft.Extensions.AI;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.Governance;

/// <summary>
/// Verifies the governed tool-function wrapper consults the ambient classification gate after the governor:
/// a block returns the gate's message without running the tool, a redact verdict runs the tool and scrubs
/// its output, an allow runs the tool unchanged, and no ambient gate passes through.
/// </summary>
public sealed class GovernedAIFunctionClassificationTests : IDisposable
{
    public void Dispose()
    {
        ToolGovernanceAccessor.Current = null;
        ClassificationGateAccessor.Current = null;
    }

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

    [Fact]
    public async Task InvokeAsync_ClassificationBlocks_ReturnsMessageAndSkipsInner()
    {
        var (inner, wasInvoked) = MakeInner();
        var gate = GateReturning(ClassificationVerdict.Block("Error: tool 'file_system' is not permitted: restricted data."));
        ClassificationGateAccessor.Current = gate.Object;

        var governed = new GovernedAIFunction(inner);
        var result = await governed.InvokeAsync(new AIFunctionArguments(), CancellationToken.None);

        Assert.False(wasInvoked(), "inner tool must not run when classification blocks");
        Assert.Contains("not permitted", result?.ToString());
    }

    [Fact]
    public async Task InvokeAsync_ClassificationRedacts_RunsInnerThenScrubsOutput()
    {
        var (inner, wasInvoked) = MakeInner();
        var gate = GateReturning(ClassificationVerdict.RedactOutput());
        gate.Setup(g => g.RedactResult("file_system", It.IsAny<object?>())).Returns("[redacted]");
        ClassificationGateAccessor.Current = gate.Object;

        var governed = new GovernedAIFunction(inner);
        var result = await governed.InvokeAsync(new AIFunctionArguments(), CancellationToken.None);

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
        ClassificationGateAccessor.Current = gate.Object;

        var governed = new GovernedAIFunction(inner);
        var result = await governed.InvokeAsync(new AIFunctionArguments(), CancellationToken.None);

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
        ClassificationGateAccessor.Current = gate.Object;
        var progress = new Mock<IProgressEvaluator>();
        ProgressGuardAccessor.Current = progress.Object;

        try
        {
            var governed = new GovernedAIFunction(inner);
            await governed.InvokeAsync(new AIFunctionArguments(), CancellationToken.None);

            progress.Verify(p => p.Evaluate(It.IsAny<string>(), It.IsAny<Func<string?>>()), Times.Never,
                "a classification-blocked call must not reach the progress guard");
        }
        finally
        {
            ProgressGuardAccessor.Current = null;
        }
    }

    [Fact]
    public async Task InvokeAsync_NoAmbientGate_PassesThrough()
    {
        var (inner, wasInvoked) = MakeInner();
        ClassificationGateAccessor.Current = null;

        var governed = new GovernedAIFunction(inner);
        var result = await governed.InvokeAsync(new AIFunctionArguments(), CancellationToken.None);

        Assert.True(wasInvoked());
        Assert.Equal("inner-result", result?.ToString());
    }
}
