using Application.AI.Common.Interfaces.Escalation;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Services.Governance;
using Application.AI.Common.Services.Tools;
using Domain.AI.Escalation;
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

    /// <summary>
    /// An inner function shaped like what <c>AIToolConverter</c> produces for a <c>ToolResult.Fail</c>:
    /// a <see cref="ConvertedToolFailure"/> return, paired with the same <c>MarshalResult</c> override
    /// that lets the marker reach this layer intact instead of being JSON-serialized away — see
    /// <see cref="ConvertedToolFailure"/>'s remarks for why that pairing is required.
    /// </summary>
    private static AIFunction MakeFailingInner(string errorText) =>
        AIFunctionFactory.Create(
            () => new ConvertedToolFailure(errorText),
            new AIFunctionFactoryOptions
            {
                Name = "file_system",
                Description = "test tool",
                MarshalResult = (result, _, _) => new ValueTask<object?>(result)
            });

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

    // ===== #325 execution reporting =====

    private static ApprovedCall ApprovedCall() =>
        new(Guid.NewGuid(), new ApprovalFailureKey("conv-1", "agent-1", "file_system"));

    private static Mock<IToolCallAdmissionPipeline> ApprovingPipeline(ApprovedCall call)
    {
        var pipeline = new Mock<IToolCallAdmissionPipeline>();
        pipeline
            .Setup(p => p.AdmitAsync(It.IsAny<ToolCallAdmissionRequest>(), It.IsAny<CancellationToken>()))
            .Returns(ValueTask.FromResult(ToolCallAdmission.Allow().WithApproval(call)));
        pipeline
            .Setup(p => p.ApplyOutputPolicy(It.IsAny<ToolCallAdmission>(), It.IsAny<string>(), It.IsAny<object?>()))
            .Returns<ToolCallAdmission, string, object?>((_, _, result) => result);
        return pipeline;
    }

    // GovernedAIFunction does not itself decide whether an approval exists to report against —
    // that no-op belongs to IToolCallAdmissionPipeline.ReportExecutionAsync (pinned in
    // ToolCallAdmissionPipelineTests), so this layer's contract is simply "always ask" on every
    // admitted call. The two tests below, against a raw mocked pipeline that has no such no-op
    // built in, are what prove the call happens on every path (success and failure).

    [Fact]
    public async Task InvokeAsync_ToolSucceeds_WithApproval_ReportsSucceeded()
    {
        var (inner, _) = MakeInner();
        var call = ApprovedCall();
        var pipeline = ApprovingPipeline(call);

        await InvokeUnder(pipeline.Object, inner);

        pipeline.Verify(
            p => p.ReportExecutionAsync(
                It.IsAny<ToolCallAdmission>(),
                It.Is<ToolExecutionReport>(r => r.Status == EscalationExecutionStatus.Succeeded),
                "agent-turn", CancellationToken.None),
            Times.Once);
    }

    [Fact]
    public async Task InvokeAsync_ToolReturnsConvertedFailure_WithApproval_ReportsFailedWithReason()
    {
        // #441: a no-throw ToolResult.Fail, flattened by AIToolConverter into a plain string, used
        // to be indistinguishable from a genuine success — this is the case that used to report
        // Succeeded. The marker is how AIToolConverter tells this layer the call actually failed.
        var inner = MakeFailingInner("Error: boom");
        var call = ApprovedCall();
        var pipeline = ApprovingPipeline(call);

        var result = await InvokeUnder(pipeline.Object, inner);

        pipeline.Verify(
            p => p.ReportExecutionAsync(
                It.IsAny<ToolCallAdmission>(),
                It.Is<ToolExecutionReport>(r =>
                    r.Status == EscalationExecutionStatus.Failed && r.FailureReason == "Error: boom"),
                "agent-turn", CancellationToken.None),
            Times.Once);
        // The model-facing text is unaffected by the marker — unwrapped back to the tool's own plain
        // error text before being returned, never the ConvertedToolFailure wrapper itself. Re-wrapped
        // as a JsonElement, the same shape a genuine success already has: the OpenAI chat client
        // sends a raw string verbatim but re-quotes anything else, so a bare string here would have
        // sent the model differently-formatted text for a failure than for a success.
        var resultStr = result is System.Text.Json.JsonElement je ? je.GetString() : result?.ToString();
        Assert.Equal("Error: boom", resultStr);
    }

    [Fact]
    public async Task InvokeAsync_ToolReturnsConvertedFailure_ReturnsSameRuntimeShapeAsSuccess()
    {
        // The OpenAI chat client (Microsoft.Extensions.AI.OpenAI's OpenAIChatClient) sends
        // FunctionResultContent.Result to the model verbatim when it's a raw CLR string, but
        // JSON-serializes (re-quoting) anything else, including a JsonElement. A genuine success
        // already reaches the model as a JsonElement (AIToolConverter's default marshaling) — if a
        // failure unwrapped to a bare string instead, the model would see differently-quoted text
        // for a failure than for a success, contradicting Unwrap's own documented contract.
        var (successInner, _) = MakeInner();
        var failureInner = MakeFailingInner("Error: boom");

        var successResult = await InvokeUnder(ApprovingPipeline(ApprovedCall()).Object, successInner);
        var failureResult = await InvokeUnder(ApprovingPipeline(ApprovedCall()).Object, failureInner);

        Assert.IsType<System.Text.Json.JsonElement>(successResult);
        Assert.IsType<System.Text.Json.JsonElement>(failureResult);
    }

    [Fact]
    public async Task InvokeAsync_NoAmbientChain_UnwrapsConvertedFailureToPlainText()
    {
        // The ungoverned bypass path never reports anything (no admission chain to report against),
        // but it must still unwrap the marker before returning — otherwise an internal type leaks
        // out to whatever called this AIFunction directly.
        var inner = MakeFailingInner("Error: boom");

        var result = await new GovernedAIFunction(inner)
            .InvokeAsync(new AIFunctionArguments(), CancellationToken.None);

        var resultStr = result is System.Text.Json.JsonElement je ? je.GetString() : result?.ToString();
        Assert.Equal("Error: boom", resultStr);
    }

    [Fact]
    public async Task InvokeAsync_ToolThrows_WithApproval_ReportsFailedAndStillRethrows()
    {
        Func<string> throwing = () => throw new InvalidOperationException("boom");
        var inner = AIFunctionFactory.Create(
            throwing, new AIFunctionFactoryOptions { Name = "file_system", Description = "test tool" });
        var call = ApprovedCall();
        var pipeline = ApprovingPipeline(call);

        Func<Task> act = () => InvokeUnder(pipeline.Object, inner);

        // MEAI's AIFunctionFactory wraps the delegate's throw in an AIFunctionArgumentException; the
        // wiring under test only needs to prove the report happened and something still escaped.
        await Assert.ThrowsAnyAsync<Exception>(act);
        pipeline.Verify(
            p => p.ReportExecutionAsync(
                It.IsAny<ToolCallAdmission>(),
                It.Is<ToolExecutionReport>(r => r.Status == EscalationExecutionStatus.Failed),
                "agent-turn", CancellationToken.None),
            Times.Once);
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
