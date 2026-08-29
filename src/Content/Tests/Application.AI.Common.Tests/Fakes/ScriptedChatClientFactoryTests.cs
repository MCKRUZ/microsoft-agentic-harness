using Application.AI.Common.Services.Agent;
using Domain.Common.Config.AI;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Tests.AI.Fakes;
using Xunit;

namespace Application.AI.Common.Tests.Fakes;

/// <summary>
/// Proves the shared <see cref="ScriptedChatClientFactory"/>/<see cref="RecordingChatClient"/> fake
/// (Tests.AI.Fakes) actually does what Packages B, C, and D of the verification cluster depend on:
/// role selection by ambient agent identity, a fail-loud unscripted-role path, per-call invocation
/// recording (including whether a structured-output schema was attached), and correct multi-frame
/// streaming. Drives the real <see cref="AgentExecutionContext"/> rather than a mock — the fake's
/// whole point is to sit downstream of the real ambient-context wiring.
/// </summary>
public sealed class ScriptedChatClientFactoryTests
{
    private static AgentExecutionContext ContextFor(string agentId)
    {
        var context = new AgentExecutionContext();
        context.Initialize(agentId, conversationId: "conv-1", turnNumber: 1);
        return context;
    }

    [Fact]
    public async Task GetChatClientAsync_UnscriptedRole_ThrowsNamingUnmatchedIdAndKnownRoles()
    {
        var log = new ChatInvocationLog();
        var factory = new ScriptedChatClientFactory(ContextFor("planner"), log);
        factory.ForRole("verifier").Enqueue("known role response");

        var act = () => factory.GetChatClientAsync(AIAgentFrameworkClientType.AzureOpenAI, "deployment");

        var exception = await act.Should().ThrowAsync<InvalidOperationException>();
        exception.Which.Message.Should().Contain("planner").And.Contain("verifier");
    }

    [Fact]
    public async Task GetChatClientAsync_NoRolesRegisteredAtAll_ThrowsNamingNoneRegistered()
    {
        var factory = new ScriptedChatClientFactory(ContextFor("planner"), new ChatInvocationLog());

        var act = () => factory.GetChatClientAsync(AIAgentFrameworkClientType.AzureOpenAI, "deployment");

        var exception = await act.Should().ThrowAsync<InvalidOperationException>();
        exception.Which.Message.Should().Contain("none registered");
    }

    [Fact]
    public async Task ForAnyUnscriptedRole_ExplicitOptIn_AcceptsAnyUnmatchedRole()
    {
        var log = new ChatInvocationLog();
        var factory = new ScriptedChatClientFactory(ContextFor("some-other-agent"), log);
        factory.ForAnyUnscriptedRole().WithDefaultResponse("fallback content");

        var client = await factory.GetChatClientAsync(AIAgentFrameworkClientType.AzureOpenAI, "deployment");
        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);

        response.Text.Should().Be("fallback content");
    }

    [Fact]
    public async Task GetResponseAsync_RecordsAgentIdMessageCountAndResponseFormatPresence()
    {
        var log = new ChatInvocationLog();
        var factory = new ScriptedChatClientFactory(ContextFor("planner"), log);
        factory.ForRole("planner").Enqueue("plan output");
        var client = await factory.GetChatClientAsync(AIAgentFrameworkClientType.AzureOpenAI, "deployment");

        var options = new ChatOptions { ResponseFormat = ChatResponseFormat.Json };
        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.System, "sys"), new ChatMessage(ChatRole.User, "go")],
            options);

        log.Invocations.Should().ContainSingle();
        var invocation = log.Invocations[0];
        invocation.AgentId.Should().Be("planner");
        invocation.MessageCount.Should().Be(2);
        invocation.HadResponseFormat.Should().BeTrue();
    }

    [Fact]
    public async Task GetResponseAsync_NoResponseFormatSupplied_RecordsFalse()
    {
        var log = new ChatInvocationLog();
        var factory = new ScriptedChatClientFactory(ContextFor("planner"), log);
        factory.ForRole("planner").Enqueue("plan output");
        var client = await factory.GetChatClientAsync(AIAgentFrameworkClientType.AzureOpenAI, "deployment");

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "go")]);

        log.Invocations[0].HadResponseFormat.Should().BeFalse();
    }

    [Fact]
    public async Task RoleSequence_AcrossMultipleAgents_PreservesCallOrder()
    {
        var log = new ChatInvocationLog();

        var plannerFactory = new ScriptedChatClientFactory(ContextFor("planner"), log);
        plannerFactory.ForRole("planner").Enqueue("plan");
        var plannerClient = await plannerFactory.GetChatClientAsync(AIAgentFrameworkClientType.AzureOpenAI, "d");
        await plannerClient.GetResponseAsync([new ChatMessage(ChatRole.User, "1")]);

        var verifierFactory = new ScriptedChatClientFactory(ContextFor("verifier"), log);
        verifierFactory.ForRole("verifier").Enqueue("verified");
        var verifierClient = await verifierFactory.GetChatClientAsync(AIAgentFrameworkClientType.AzureOpenAI, "d");
        await verifierClient.GetResponseAsync([new ChatMessage(ChatRole.User, "2")]);

        log.RoleSequence.Should().Equal("planner", "verifier");
    }

    [Fact]
    public async Task RoleScript_QueueExhausted_FallsBackToDefaultResponse()
    {
        var factory = new ScriptedChatClientFactory(ContextFor("planner"), new ChatInvocationLog());
        factory.ForRole("planner").Enqueue("first").WithDefaultResponse("steady state");
        var client = await factory.GetChatClientAsync(AIAgentFrameworkClientType.AzureOpenAI, "d");

        var first = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "1")]);
        var second = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "2")]);

        first.Text.Should().Be("first");
        second.Text.Should().Be("steady state");
    }

    [Fact]
    public async Task EnqueueThrow_ThrowsTheScriptedExceptionOnThatCall()
    {
        var factory = new ScriptedChatClientFactory(ContextFor("planner"), new ChatInvocationLog());
        factory.ForRole("planner").EnqueueThrow(new InvalidOperationException("provider unavailable"));
        var client = await factory.GetChatClientAsync(AIAgentFrameworkClientType.AzureOpenAI, "d");

        var act = () => client.GetResponseAsync([new ChatMessage(ChatRole.User, "1")]);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("provider unavailable");
    }

    [Fact]
    public async Task GetStreamingResponseAsync_EmitsOneFramePerContentItemThenTrailingUsage()
    {
        // The control this fake exists to satisfy: a real streaming sequence, not one collapsed
        // frame — Package D's per-frame invariants would pass vacuously against a single-frame fake.
        var factory = new ScriptedChatClientFactory(ContextFor("planner"), new ChatInvocationLog());
        factory.ForRole("planner").EnqueueWithUsage("hello world", inputTokens: 10, outputTokens: 5);
        var client = await factory.GetChatClientAsync(AIAgentFrameworkClientType.AzureOpenAI, "d");

        var frames = new List<ChatResponseUpdate>();
        await foreach (var frame in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "go")]))
            frames.Add(frame);

        frames.Should().HaveCount(2, "one text content frame, then one trailing usage frame");
        frames[0].Contents.Should().ContainSingle().Which.Should().BeOfType<TextContent>();
        frames[1].Contents.Should().ContainSingle().Which.Should().BeOfType<UsageContent>();
    }

    [Fact]
    public async Task GetStreamingResponseAsync_ToolCallResponse_StreamsTheFunctionCallAsItsOwnFrame()
    {
        var factory = new ScriptedChatClientFactory(ContextFor("planner"), new ChatInvocationLog());
        factory.ForRole("planner").EnqueueToolCall("search", "call-1");
        var client = await factory.GetChatClientAsync(AIAgentFrameworkClientType.AzureOpenAI, "d");

        var frames = new List<ChatResponseUpdate>();
        await foreach (var frame in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "go")]))
            frames.Add(frame);

        frames.Should().ContainSingle().Which.Contents.Should().ContainSingle()
            .Which.Should().BeOfType<FunctionCallContent>();
    }
}
