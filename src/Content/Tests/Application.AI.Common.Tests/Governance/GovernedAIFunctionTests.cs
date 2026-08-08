using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Services.Governance;
using Application.AI.Common.Services.Tools;
using Microsoft.Extensions.AI;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.Governance;

/// <summary>
/// Verifies the governed tool-function wrapper runs the ambient admission chain before invoking the
/// inner tool, blocks on a refusal without calling the tool, and passes through when nothing is armed.
/// </summary>
public sealed class GovernedAIFunctionTests
{
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
        return await new GovernedAIFunction(inner).InvokeAsync(new AIFunctionArguments(), CancellationToken.None);
    }

    [Fact]
    public async Task InvokeAsync_GovernorDenies_ReturnsDeniedMessageAndSkipsInner()
    {
        var (inner, wasInvoked) = MakeInner();
        var governor = new Mock<IToolInvocationGovernor>();
        governor
            .Setup(g => g.AuthorizeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyDictionary<string, object?>?>()))
            .ReturnsAsync(ToolInvocationDecision.Deny("Error: tool 'file_system' was blocked by governance — denied."));

        var result = await InvokeUnder(AdmissionHarness.Pipeline(governor: governor.Object), inner);

        Assert.False(wasInvoked(), "inner tool must not run when governance denies");
        Assert.Contains("blocked by governance", result?.ToString());
    }

    [Fact]
    public async Task InvokeAsync_GovernorAllows_InvokesInner()
    {
        var (inner, wasInvoked) = MakeInner();

        await InvokeUnder(AdmissionHarness.Pipeline(), inner);

        Assert.True(wasInvoked(), "inner tool must run when governance allows");
    }

    [Fact]
    public async Task InvokeAsync_NoAmbientChain_PassesThrough()
    {
        var (inner, wasInvoked) = MakeInner();

        await new GovernedAIFunction(inner).InvokeAsync(new AIFunctionArguments(), CancellationToken.None);

        Assert.True(wasInvoked(), "inner tool must run when no admission chain is ambient");
    }

    [Fact]
    public async Task InvokeAsync_AsksForLoopDetection_UnlikeEveryOtherCaller()
    {
        // The agent turn is the ONE caller that issues a repeatable series of tool calls in a single
        // unit of work, so it is the one caller that opts into the loop guard. If this ever stopped
        // being set, the spin detector would go quiet everywhere and every other test here would still
        // pass — the guard's stages are all permissive by default.
        var (inner, _) = MakeInner();
        ToolCallAdmissionRequest? seen = null;
        var pipeline = new Mock<IToolCallAdmissionPipeline>();
        pipeline
            .Setup(p => p.AdmitAsync(It.IsAny<ToolCallAdmissionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<ToolCallAdmissionRequest, CancellationToken>((r, _) => seen = r)
            .Returns(ValueTask.FromResult(ToolCallAdmission.Allow()));
        pipeline
            .Setup(p => p.ApplyOutputPolicy(It.IsAny<ToolCallAdmission>(), It.IsAny<string>(), It.IsAny<object?>()))
            .Returns<ToolCallAdmission, string, object?>((_, _, result) => result);

        await InvokeUnder(pipeline.Object, inner);

        Assert.NotNull(seen);
        Assert.True(seen!.CountsTowardLoopDetection);
        Assert.Equal("file_system", seen.ToolName);
    }

    [Fact]
    public void Decorator_PreservesInnerNameAndSchema()
    {
        var (inner, _) = MakeInner();
        var governed = new GovernedAIFunction(inner);

        Assert.Equal(inner.Name, governed.Name);
        Assert.Equal(inner.JsonSchema.ToString(), governed.JsonSchema.ToString());
    }
}
