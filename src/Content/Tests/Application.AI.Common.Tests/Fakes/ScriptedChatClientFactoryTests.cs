using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Services;
using Application.AI.Common.Services.Agent;
using Domain.Common.Config.AI;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Tests.AI.Fakes;
using Xunit;

namespace Application.AI.Common.Tests.Fakes;

/// <summary>
/// Proves the shared <see cref="ScriptedChatClientFactory"/>/<see cref="RecordingChatClient"/> fake
/// (Tests.AI.Fakes) actually does what Packages B, C, and D of the verification cluster depend on:
/// role selection by ambient agent identity, a fail-loud unscripted-role path, per-call invocation
/// recording (including whether a structured-output schema was attached), and correct multi-frame
/// streaming.
/// </summary>
/// <remarks>
/// Drives the real <see cref="AgentExecutionContext"/> and the real <see cref="AmbientRequestScope"/>
/// — the same <see cref="IAmbientRequestScope"/> bridge production singletons use to reach per-request
/// scoped services — rather than a mock, since the fake's whole point is to sit downstream of that
/// real ambient-scope wiring, not a shortcut around it.
/// </remarks>
public sealed class ScriptedChatClientFactoryTests
{
    /// <summary>
    /// Establishes the ambient request scope a real <c>AmbientRequestScopeBehavior&lt;,&gt;</c> would
    /// set up for one request, with a real <see cref="AgentExecutionContext"/> registered as the
    /// scope would have it (matching production's <c>AddScoped&lt;IAgentExecutionContext,
    /// AgentExecutionContext&gt;()</c>). The returned token must be disposed to clear the ambient
    /// value — callers use <see langword="using"/>.
    /// </summary>
    private static (ScriptedChatClientFactory Factory, ChatInvocationLog Log, IDisposable ScopeToken) CreateForAgent(string agentId)
    {
        var context = new AgentExecutionContext();
        context.Initialize(agentId, conversationId: "conv-1", turnNumber: 1);

        var services = new ServiceCollection();
        services.AddSingleton<IAgentExecutionContext>(context);
        var provider = services.BuildServiceProvider();

        var ambientScope = new AmbientRequestScope();
        var token = ambientScope.BeginScope(provider);

        var log = new ChatInvocationLog();
        var factory = new ScriptedChatClientFactory(ambientScope, log);
        return (factory, log, token);
    }

    [Fact]
    public async Task GetChatClientAsync_UnscriptedRole_ThrowsNamingUnmatchedIdAndKnownRoles()
    {
        var (factory, _, scope) = CreateForAgent("planner");
        using var _ = scope;
        factory.ForRole("verifier").Enqueue("known role response");

        var act = () => factory.GetChatClientAsync(AIAgentFrameworkClientType.AzureOpenAI, "deployment");

        var exception = await act.Should().ThrowAsync<InvalidOperationException>();
        exception.Which.Message.Should().Contain("planner").And.Contain("verifier");
    }

    [Fact]
    public async Task GetChatClientAsync_NoRolesRegisteredAtAll_ThrowsNamingNoneRegistered()
    {
        var (factory, _, scope) = CreateForAgent("planner");
        using var _ = scope;

        var act = () => factory.GetChatClientAsync(AIAgentFrameworkClientType.AzureOpenAI, "deployment");

        var exception = await act.Should().ThrowAsync<InvalidOperationException>();
        exception.Which.Message.Should().Contain("none registered");
    }

    [Fact]
    public async Task GetChatClientAsync_NoAmbientScopeEstablished_ThrowsDistinctlyFromUnscriptedRole()
    {
        // A factory that is never handed an active ambient scope — distinct misconfiguration from
        // "scope exists but no script matches", and must say so, not report agent id '<null>'.
        var factory = new ScriptedChatClientFactory(new AmbientRequestScope(), new ChatInvocationLog());

        var act = () => factory.GetChatClientAsync(AIAgentFrameworkClientType.AzureOpenAI, "deployment");

        var exception = await act.Should().ThrowAsync<InvalidOperationException>();
        exception.Which.Message.Should().Contain("no ambient request scope is established");
    }

    [Fact]
    public async Task GetChatClientAsync_AmbientScopeWithNoExecutionContextRegistered_ThrowsDistinctly()
    {
        var ambientScope = new AmbientRequestScope();
        using var token = ambientScope.BeginScope(new ServiceCollection().BuildServiceProvider());
        var factory = new ScriptedChatClientFactory(ambientScope, new ChatInvocationLog());

        var act = () => factory.GetChatClientAsync(AIAgentFrameworkClientType.AzureOpenAI, "deployment");

        var exception = await act.Should().ThrowAsync<InvalidOperationException>();
        exception.Which.Message.Should().Contain("no IAgentExecutionContext registered");
    }

    [Fact]
    public async Task ForAnyUnscriptedRole_ExplicitOptIn_AcceptsAnyUnmatchedRole()
    {
        var (factory, _, scope) = CreateForAgent("some-other-agent");
        using var _ = scope;
        factory.ForAnyUnscriptedRole().WithDefaultResponse("fallback content");

        var client = await factory.GetChatClientAsync(AIAgentFrameworkClientType.AzureOpenAI, "deployment");
        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);

        response.Text.Should().Be("fallback content");
    }

    [Fact]
    public async Task ForAnyUnscriptedRole_CalledTwice_ReturnsSameScriptRatherThanDiscardingQueuedItems()
    {
        var (factory, _, scope) = CreateForAgent("some-other-agent");
        using var _ = scope;
        factory.ForAnyUnscriptedRole().Enqueue("first queued");
        factory.ForAnyUnscriptedRole().Enqueue("second queued");
        var client = await factory.GetChatClientAsync(AIAgentFrameworkClientType.AzureOpenAI, "deployment");

        var first = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "1")]);
        var second = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "2")]);

        first.Text.Should().Be("first queued");
        second.Text.Should().Be("second queued");
    }

    [Fact]
    public async Task GetResponseAsync_RecordsAgentIdMessageCountAndResponseFormatPresence()
    {
        var (factory, log, scope) = CreateForAgent("planner");
        using var _ = scope;
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
        var (factory, log, scope) = CreateForAgent("planner");
        using var _ = scope;
        factory.ForRole("planner").Enqueue("plan output");
        var client = await factory.GetChatClientAsync(AIAgentFrameworkClientType.AzureOpenAI, "deployment");

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "go")]);

        log.Invocations[0].HadResponseFormat.Should().BeFalse();
    }

    [Fact]
    public async Task RoleSequence_AcrossMultipleAgents_PreservesCallOrder()
    {
        var log = new ChatInvocationLog();

        var (plannerFactory, _, plannerScope) = CreateForAgentSharingLog("planner", log);
        using (plannerScope)
        {
            plannerFactory.ForRole("planner").Enqueue("plan");
            var plannerClient = await plannerFactory.GetChatClientAsync(AIAgentFrameworkClientType.AzureOpenAI, "d");
            await plannerClient.GetResponseAsync([new ChatMessage(ChatRole.User, "1")]);
        }

        var (verifierFactory, _, verifierScope) = CreateForAgentSharingLog("verifier", log);
        using (verifierScope)
        {
            verifierFactory.ForRole("verifier").Enqueue("verified");
            var verifierClient = await verifierFactory.GetChatClientAsync(AIAgentFrameworkClientType.AzureOpenAI, "d");
            await verifierClient.GetResponseAsync([new ChatMessage(ChatRole.User, "2")]);
        }

        log.RoleSequence.Should().Equal("planner", "verifier");
    }

    private static (ScriptedChatClientFactory Factory, ChatInvocationLog Log, IDisposable ScopeToken) CreateForAgentSharingLog(
        string agentId, ChatInvocationLog log)
    {
        var context = new AgentExecutionContext();
        context.Initialize(agentId, conversationId: "conv-1", turnNumber: 1);
        var services = new ServiceCollection();
        services.AddSingleton<IAgentExecutionContext>(context);
        var provider = services.BuildServiceProvider();
        var ambientScope = new AmbientRequestScope();
        var token = ambientScope.BeginScope(provider);
        return (new ScriptedChatClientFactory(ambientScope, log), log, token);
    }

    [Fact]
    public async Task RoleScript_QueueExhausted_FallsBackToDefaultResponse()
    {
        var (factory, _, scope) = CreateForAgent("planner");
        using var _ = scope;
        factory.ForRole("planner").Enqueue("first").WithDefaultResponse("steady state");
        var client = await factory.GetChatClientAsync(AIAgentFrameworkClientType.AzureOpenAI, "d");

        var first = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "1")]);
        var second = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "2")]);
        var third = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "3")]);

        first.Text.Should().Be("first");
        second.Text.Should().Be("steady state");
        third.Text.Should().Be("steady state");
        // The control for L4: the default response must be a fresh object each time, not one
        // shared mutable instance a caller's middleware could mutate and leak into the next call.
        ReferenceEquals(second.Messages, third.Messages).Should().BeFalse();
    }

    [Fact]
    public async Task EnqueueThrow_ThrowsOnceThenFallsThroughToWhateverIsNext()
    {
        // EnqueueThrow is documented as non-sticky: it consumes its queue slot like any other
        // scripted item. A caller that needs every call to fail uses AlwaysThrow instead.
        var (factory, _, scope) = CreateForAgent("planner");
        using var _ = scope;
        factory.ForRole("planner")
            .EnqueueThrow(new InvalidOperationException("provider unavailable"))
            .Enqueue("recovered");
        var client = await factory.GetChatClientAsync(AIAgentFrameworkClientType.AzureOpenAI, "d");

        var act = () => client.GetResponseAsync([new ChatMessage(ChatRole.User, "1")]);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("provider unavailable");

        var recovered = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "2")]);
        recovered.Text.Should().Be("recovered");
    }

    [Fact]
    public async Task AlwaysThrow_FailsEveryCallIncludingAfterQueueExhaustion()
    {
        var (factory, _, scope) = CreateForAgent("planner");
        using var _ = scope;
        factory.ForRole("planner").AlwaysThrow(new InvalidOperationException("total failure"));
        var client = await factory.GetChatClientAsync(AIAgentFrameworkClientType.AzureOpenAI, "d");

        var first = () => client.GetResponseAsync([new ChatMessage(ChatRole.User, "1")]);
        var second = () => client.GetResponseAsync([new ChatMessage(ChatRole.User, "2")]);

        await first.Should().ThrowAsync<InvalidOperationException>();
        await second.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task GetStreamingResponseAsync_EmitsOneFramePerContentItemThenTrailingUsage()
    {
        // The control this fake exists to satisfy: a real streaming sequence, not one collapsed
        // frame — Package D's per-frame invariants would pass vacuously against a single-frame fake.
        var (factory, _, scope) = CreateForAgent("planner");
        using var _ = scope;
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
        var (factory, _, scope) = CreateForAgent("planner");
        using var _ = scope;
        factory.ForRole("planner").EnqueueToolCall("search", "call-1");
        var client = await factory.GetChatClientAsync(AIAgentFrameworkClientType.AzureOpenAI, "d");

        var frames = new List<ChatResponseUpdate>();
        await foreach (var frame in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "go")]))
            frames.Add(frame);

        frames.Should().ContainSingle().Which.Contents.Should().ContainSingle()
            .Which.Should().BeOfType<FunctionCallContent>();
    }
}
