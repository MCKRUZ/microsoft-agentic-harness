using Application.AI.Common.Tests.Fakes;
using FluentAssertions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;

namespace Application.AI.Common.Tests.FunctionInvocation;

/// <summary>
/// The gate for #249 item 6 (tool-call memory across conversation turns/runs): proves — rather than
/// assumes from framework documentation — that a tool call and its result actually survive into the
/// final response object at both layers this harness's replay design needs them at. If either test
/// here fails, the design in the tracking issue's plan needs to change before any of the rest of it
/// is built; see the contingency noted on the plan (capture from inside the middleware pipeline
/// instead, which is already proven to work — it's how <c>ToolDiagnosticsMiddleware</c> gets its
/// data today).
/// </summary>
/// <remarks>
/// Deliberately a new file, not appended to <c>BlockingToolRoundTripTests.cs</c> — that file pins a
/// different property (suspend/resume behavior around a blocking tool) and its own doc header is
/// load-bearing for that purpose.
/// </remarks>
public sealed class ToolCallCaptureFeasibilityTests
{
    [Fact]
    public async Task BlockingRun_ResponseMessages_ContainFunctionCallAndResultContent()
    {
        // Layer 1: the function-invocation middleware itself, at the IChatClient level — the same
        // layer ToolDiagnosticsMiddleware already observes successfully today.
        var tool = AIFunctionFactory.Create(
            (string path) => $"contents-of-{path}", "read_file", "Reads a file.");

        var pipeline = new FakeChatClient()
            .EnqueueResponseWithToolCall(
                "read_file", "call-1", new Dictionary<string, object?> { ["path"] = "/tmp/x" })
            .EnqueueResponse("done")
            .AsBuilder()
            .UseFunctionInvocation()
            .Build();

        var response = await pipeline.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "go")],
            new ChatOptions { Tools = [tool] });

        var calls = response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().ToList();
        var results = response.Messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().ToList();

        calls.Should().ContainSingle().Which.CallId.Should().Be("call-1");
        calls[0].Arguments.Should().NotBeNull().And.ContainKey("path");
        results.Should().ContainSingle().Which.CallId.Should().Be("call-1");
        results[0].Result!.ToString().Should().Contain("contents-of-/tmp/x");
    }

    [Fact]
    public async Task ChatClientAgentRunAsync_AgentResponseMessages_PreserveToolCallAndResult()
    {
        // Layer 2: the actual production construction path — AgentFactory.CreateChatClientAgentAsync
        // builds a ChatClientAgent over the same middleware-wrapped IChatClient, with tools supplied
        // via ChatClientAgentOptions.ChatOptions.Tools (AgentFactory.cs:134-141). This is the layer
        // #249 item 6's turn handler actually reads a response from — if tool content doesn't survive
        // here, it doesn't matter that it survives at Layer 1.
        var tool = AIFunctionFactory.Create(
            (string path) => $"contents-of-{path}", "read_file", "Reads a file.");

        var pipeline = new FakeChatClient()
            .EnqueueResponseWithToolCall(
                "read_file", "call-1", new Dictionary<string, object?> { ["path"] = "/tmp/x" })
            .EnqueueResponse("done")
            .AsBuilder()
            .UseFunctionInvocation()
            .Build();

        var agent = new ChatClientAgent(pipeline, new ChatClientAgentOptions
        {
            ChatOptions = new ChatOptions { Tools = [tool] }
        });

        var response = await agent.RunAsync([new ChatMessage(ChatRole.User, "go")]);

        response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>()
            .Should().ContainSingle("AgentResponse must carry the tool call, not just the final text");
        response.Messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>()
            .Should().ContainSingle("AgentResponse must carry the tool result");
    }
}
