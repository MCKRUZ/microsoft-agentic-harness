using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Distributed;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Pipeline;

/// <summary>
/// Guards against runtime TypeLoadExceptions caused by Microsoft.Extensions.AI
/// package version mismatches. The specific bug: FunctionInvokingChatClient.HasAnyApprovalContent
/// tries to load FunctionApprovalRequestContent from ME.AI.Abstractions at runtime —
/// when ME.AI and ME.AI.Abstractions resolve to different versions, this throws TypeLoadException.
///
/// Root cause: ME.AI.Abstractions was pinned to 10.3.0 in CPM while a transitive dependency
/// (Microsoft.Extensions.AI.AzureAIInference preview) pulled ME.AI.Abstractions to 10.4.1.
/// ME.AI 10.3.0 referenced FunctionApprovalRequestContent but the resolved 10.4.1 assembly
/// did not contain it. Fix: pin both packages to 10.4.1.
/// </summary>
public class MeAiPipelineCompatibilityTests
{
    /// <summary>
    /// Regression guard for the TypeLoadException on FunctionApprovalRequestContent.
    /// FunctionInvokingChatClient.HasAnyApprovalContent is called on every GetResponseAsync —
    /// this test fails immediately if ME.AI and ME.AI.Abstractions are version-mismatched.
    /// </summary>
    [Fact]
    public async Task FunctionInvokingPipeline_SimpleResponse_ExecutesWithoutTypeLoadException()
    {
        // Arrange — same middleware as AgentFactory.CreateAgentAsync
        var client = new FakeChatClient("Pipeline response").AsBuilder()
            .UseFunctionInvocation(configure: c =>
            {
                c.AllowConcurrentInvocation = true;
                c.MaximumIterationsPerRequest = 5;
            })
            .Build();

        // Act — FunctionInvokingChatClient.HasAnyApprovalContent fires here;
        // throws TypeLoadException when ME.AI / ME.AI.Abstractions versions diverge
        var act = async () => await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "Hello")]);

        // Assert
        await act.Should().NotThrowAsync<TypeLoadException>();
    }

    [Fact]
    public async Task FunctionInvokingPipeline_SimpleResponse_ReturnsAssistantText()
    {
        // Arrange
        var client = new FakeChatClient("Hello from the pipeline").AsBuilder()
            .UseFunctionInvocation()
            .Build();

        // Act
        var response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "Hello")]);

        // Assert
        response.Text.Should().Be("Hello from the pipeline");
    }

    [Fact]
    public async Task FunctionInvokingPipeline_WithRegisteredTool_InvokesToolAndReturnsFinalResponse()
    {
        // Arrange
        var toolWasInvoked = false;
        var tool = AIFunctionFactory.Create(
            () => { toolWasInvoked = true; return "tool result"; },
            "echo_tool",
            "Echoes a test result");

        // Sequence: first call triggers a tool call, second call returns the final answer
        var callSequence = new Queue<ChatResponse>(
        [
            new ChatResponse([new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent("call-1", "echo_tool", new Dictionary<string, object?>())])]),
            new ChatResponse([new ChatMessage(ChatRole.Assistant, "Final answer after tool")])
        ]);

        var pipeline = new FakeChatClient(_ => callSequence.Dequeue()).AsBuilder()
            .UseFunctionInvocation()
            .Build();

        // Act
        var response = await pipeline.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "Use the tool")],
            new ChatOptions { Tools = [tool] });

        // Assert
        toolWasInvoked.Should().BeTrue();
        response.Text.Should().Be("Final answer after tool");
    }

    /// <summary>
    /// Mirrors the exact ChatClient pipeline built in AgentFactory.CreateAgentAsync,
    /// including OpenTelemetry, function invocation, and distributed cache middleware.
    /// </summary>
    [Fact]
    public async Task FullAgentFactoryPipeline_ExactConfiguration_ExecutesWithoutException()
    {
        // Arrange — mock IDistributedCache to simulate a cache miss (delegates to inner client)
        var distributedCache = new Mock<IDistributedCache>();
        distributedCache
            .Setup(c => c.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((byte[]?)null);

        var client = new FakeChatClient("Full pipeline response").AsBuilder()
            .UseOpenTelemetry(configure: c => c.EnableSensitiveData = true)
            .UseFunctionInvocation(configure: c =>
            {
                c.AllowConcurrentInvocation = true;
                c.IncludeDetailedErrors = true;
                c.MaximumConsecutiveErrorsPerRequest = 3;
                c.MaximumIterationsPerRequest = 5;
                c.TerminateOnUnknownCalls = true;
            })
            .UseDistributedCache(distributedCache.Object)
            .Build();

        // Act
        var act = async () => await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "Run full pipeline")]);

        // Assert
        await act.Should().NotThrowAsync();
    }
}

/// <summary>
/// Minimal IChatClient for pipeline compatibility tests. Returns canned responses
/// without hitting any external AI service.
/// </summary>
file sealed class FakeChatClient : IChatClient
{
    private readonly Func<IEnumerable<ChatMessage>, ChatResponse> _handler;

    public FakeChatClient(string responseText)
        : this(_ => new ChatResponse([new ChatMessage(ChatRole.Assistant, responseText)]))
    {
    }

    public FakeChatClient(Func<IEnumerable<ChatMessage>, ChatResponse> handler)
    {
        _handler = handler;
    }

    public ChatClientMetadata Metadata { get; } = new("fake", null, null);

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult(_handler(messages));

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Streaming not used in pipeline compatibility tests.");

    public object? GetService(Type serviceType, object? key = null)
        => serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() { }
}
