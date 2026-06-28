using Application.AI.Common.Tests.Fakes;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Xunit;

namespace Application.AI.Common.Tests.Spikes;

/// <summary>
/// SPIKE (throwaway feasibility probe — not a production feature).
///
/// Question: can a model-invoked tool block mid-run while it awaits a result supplied OUT OF BAND
/// (e.g. a browser executing an AG-UI tool call and POSTing the result back), and then have the run
/// resume with that result? This is the load-bearing unknown for an AG-UI client-tool round-trip.
///
/// The existing <see cref="Infrastructure.AI.Tests.Pipeline.MeAiPipelineCompatibilityTests"/> already
/// proves the basic tool round-trip (UseFunctionInvocation invokes a registered tool, then the model
/// produces a final answer). What is unproven — and probed here — is whether the function-invocation
/// middleware tolerates a tool that does NOT return promptly but instead parks on a
/// <see cref="TaskCompletionSource{TResult}"/> completed by a separate caller.
///
/// We drive the SAME middleware the harness uses in production (<c>AgentFactory.BuildMiddlewarePipeline</c>
/// → <c>.UseFunctionInvocation()</c>) over a deterministic fake chat client, so no live LLM is needed.
/// </summary>
public class BlockingToolRoundTripSpikeTests
{
    [Fact]
    public async Task BlockingTool_SuspendsRunUntilExternallyCompleted_ThenResumes()
    {
        // A signal that the tool has begun executing (and is about to block), plus the out-of-band
        // channel a "browser" would complete with the tool result.
        var toolEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        var blockingTool = AIFunctionFactory.Create(
            async () =>
            {
                toolEntered.TrySetResult();
                return await release.Task; // parks here until completed from outside the run
            },
            "block_tool",
            "Blocks until an external caller supplies the result.");

        // First model turn calls the tool; second turn (after the tool result is fed back) answers.
        var pipeline = new FakeChatClient()
            .EnqueueResponseWithToolCall("block_tool", "call-1")
            .EnqueueResponse("done after tool")
            .AsBuilder()
            .UseFunctionInvocation()
            .Build();

        var runTask = pipeline.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "go")],
            new ChatOptions { Tools = [blockingTool] });

        // The model issued the tool call and the tool is now parked awaiting the external result.
        await toolEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        runTask.IsCompleted.Should().BeFalse("the run must stay suspended while the tool blocks");

        // Resume from outside the run — exactly like a browser POSTing back its tool result.
        release.SetResult("tool-result-payload");

        var response = await runTask.WaitAsync(TimeSpan.FromSeconds(5));
        response.Text.Should().Be("done after tool",
            "the run must resume and complete once the blocking tool is externally satisfied");
    }

    [Fact]
    public async Task BlockingTool_Cancellation_UnwindsWithoutHanging()
    {
        var toolEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();

        var blockingTool = AIFunctionFactory.Create(
            async (CancellationToken ct) =>
            {
                toolEntered.TrySetResult();
                using var reg = ct.Register(() => release.TrySetCanceled(ct));
                return await release.Task;
            },
            "block_tool",
            "Blocks until released or cancelled.");

        var pipeline = new FakeChatClient()
            .EnqueueResponseWithToolCall("block_tool", "call-1")
            .EnqueueResponse("unreachable")
            .AsBuilder()
            .UseFunctionInvocation()
            .Build();

        var runTask = pipeline.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "go")],
            new ChatOptions { Tools = [blockingTool] },
            cts.Token);

        await toolEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();

        // The key property: cancelling a blocked tool unwinds the run promptly rather than hanging.
        // Whether the framework propagates the cancellation or finishes some other way is acceptable;
        // a TimeoutException (a hung run) is not.
        var unwind = async () =>
        {
            try { await runTask.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) { /* acceptable: cancellation propagated */ }
        };
        await unwind.Should().NotThrowAsync<TimeoutException>();
    }
}
