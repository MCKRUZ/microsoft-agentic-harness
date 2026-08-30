using Application.AI.Common;
using Application.AI.Common.Categorization;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.AI;
using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Context;
using Application.AI.Common.Interfaces.Skills;
using Application.AI.Common.Interfaces.Tools;
using Application.AI.Common.Factories;
using Application.AI.Common.MediatRBehaviors;
using Application.AI.Common.Notifications;
using Application.AI.Common.Services;
using Application.AI.Common.Services.AI;
using Application.AI.Common.Services.Context;
using Application.AI.Common.Services.Skills;
using Application.AI.Common.Services.Tools;
using Application.Core.CQRS.Agents.ExecuteAgentTurn;
using Application.Core.Tests.Helpers;
using Domain.AI.Agents;
using Domain.AI.Skills;
using Domain.Common.Config;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Tests.AI.Fakes;
using Xunit;
using AppExecutionContext = Application.AI.Common.Services.Agent.AgentExecutionContext;

namespace Application.Core.Tests.CQRS;

/// <summary>
/// Proves a full agent turn works end-to-end through the REAL pipeline — MediatR, the middleware
/// behaviors, <see cref="AgentFactory"/>, <see cref="AgentExecutionContextFactory"/>, the admission
/// chain, and the agent framework's own middleware pipeline — with only the model faked.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="AgentPipelineIntegrationTests"/> proved the pipeline above <c>IAgentConversationCache</c>
/// (MediatR wiring, the double-init regression). This class moves the cut one layer lower, to
/// <see cref="IChatClientFactory"/>, so <see cref="AgentFactory.CreateAgentWithContextFromSkillsAsync"/>
/// actually runs and the resulting <c>AIAgent</c> is real — a genuine zero-LLM proof that the whole
/// graph composes, not just that a mocked collaborator was called correctly.
/// </para>
/// <para>
/// The scripted fake (<see cref="ScriptedChatClientFactory"/>) resolves a role by
/// <see cref="IAgentExecutionContext.AgentId"/>, which <see cref="AgentContextPropagationBehavior{TRequest,TResponse}"/>
/// stamps before the handler runs — this container registers that behavior, so role resolution is
/// exercised for real, not assumed.
/// </para>
/// </remarks>
public sealed class ZeroLlmPipelineTests
{
    private const string AgentName = "zero-llm-test-agent";

    private static (ServiceProvider Provider, ChatInvocationLog Log) BuildPipeline(
        Action<ScriptedChatClientFactory> configureScript)
    {
        var services = new ServiceCollection();

        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        services.AddMediatR(cfg =>
            cfg.RegisterServicesFromAssembly(typeof(ExecuteAgentTurnCommandHandler).Assembly));

        // Order matches production (Application.AI.Common/DependencyInjection.cs): the ambient
        // scope must be established BEFORE AgentContextPropagationBehavior runs, since it is what
        // makes IAgentExecutionContext reachable from the singleton-lifetime ScriptedChatClientFactory.
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(AmbientRequestScopeBehavior<,>));
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(AgentContextPropagationBehavior<,>));
        services.AddScoped<IAgentExecutionContext, AppExecutionContext>();
        services.AddSingleton<IAmbientRequestScope, AmbientRequestScope>();

        // --- The real chat-client boundary (this package's whole point) ---
        var log = new ChatInvocationLog();
        services.AddSingleton(log);
        services.AddScoped<IChatClientFactory>(sp =>
        {
            var factory = new ScriptedChatClientFactory(
                sp.GetRequiredService<IAmbientRequestScope>(), log);
            configureScript(factory);
            return factory;
        });

        // --- Real AgentFactory + AgentExecutionContextFactory (the seam this package lowers the cut to) ---
        services.AddDistributedMemoryCache();
        services.AddMemoryCache();

        var skillRegistryMock = new Mock<ISkillMetadataRegistry>();
        skillRegistryMock
            .Setup(r => r.TryGet(It.IsAny<string>()))
            .Returns((string id) => new SkillDefinition
            {
                Id = id,
                Name = id,
                Instructions = "You are a test agent used only to prove the pipeline composes.",
            });
        services.AddSingleton(skillRegistryMock.Object);

        services.AddSingleton(Mock.Of<ISkillCompletionTracker>());

        // Real ToolChainBuilder with no tool converter/MCP provider registered on the resolved
        // context — a zero-tool agent, matching this package's "prove composition" scope; tool
        // wiring through this same chain is already covered by AgentPipelineIntegrationTests.
        services.AddSingleton<IToolChainBuilder>(sp =>
            new ToolChainBuilder(NullLogger<ToolChainBuilder>.Instance, sp, toolConverter: null, mcpToolProvider: null));

        services.AddSingleton<ISkillPrerequisiteResolver>(new SkillPrerequisiteResolver());
        services.AddSingleton<ISkillFileReader>(Mock.Of<ISkillFileReader>());

        services.AddSingleton(Mock.Of<IAgentMetadataRegistry>(
            r => r.TryGet(It.IsAny<string>()) == null && r.GetAll() == Array.Empty<AgentDefinition>()));

        services.AddSingleton<AgentExecutionContextFactory>();
        services.AddSingleton<IAgentFactory, AgentFactory>();

        // Real conversation cache — the entry point this package's cut moves through, rather than
        // mocking it away as AgentPipelineIntegrationTests does.
        services.AddSingleton<IAgentConversationCache, Application.AI.Common.Services.AgentConversationCache>();
        services.AddSingleton<IConversationRegistrationTracker, ConversationRegistrationTracker>();

        // The real admission chain over five permissive gates — shared with AgentPipelineIntegrationTests
        // (see PermissiveGovernanceChain's remarks) rather than a second hand-copied ~90-line block.
        services.AddPermissiveGovernanceChain();

        services.AddSingleton(new Mock<IObservabilityStore>().Object);
        services.AddSingleton<IContextSnapshotComputer, DefaultContextSnapshotComputer>();
        services.AddSingleton<IContextSnapshotNotifier, NullContextSnapshotNotifier>();
        services.AddSingleton(TimeProvider.System);

        var usageCaptureMock = new Mock<ILlmUsageCapture>();
        usageCaptureMock.Setup(c => c.TakeSnapshot())
            .Returns(new LlmUsageSnapshot(0, 0, 0, 0, null, 0m, 0m, Array.Empty<string>()));
        services.AddScoped<ILlmUsageCapture>(_ => usageCaptureMock.Object);

        return (services.BuildServiceProvider(), log);
    }

    private static ExecuteAgentTurnCommand Command(string agentName = AgentName, int turnNumber = 1, string userMessage = "Hello") => new()
    {
        AgentName = agentName,
        UserMessage = userMessage,
        ConversationId = $"conv-{agentName}",
        TurnNumber = turnNumber,
    };

    [Fact]
    public async Task Clean_SingleRoleSucceeds_AssertsInvocationSequenceNotJustFinalResponse()
    {
        var (provider, log) = BuildPipeline(factory =>
            factory.ForRole(AgentName).WithDefaultResponse("Hello from the real pipeline"));
        using var providerDisposal = provider;
        using var scope = provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var result = await mediator.Send(Command());

        result.Success.Should().BeTrue(result.Error);
        result.Response.Should().Contain("Hello from the real pipeline");
        log.RoleSequence.Should().Equal(AgentName);
    }

    [Fact]
    public async Task MultiTurn_SecondTurnOnSameConversation_DoesNotClobberEitherTurnsOutput()
    {
        // #322's original scenario here was "one role returns malformed output, Package B's repair
        // recovers it" — verified against the real ExecuteAgentTurnCommandHandler (see the type's
        // remarks): a plain agent turn has no repair loop at all. Package B's structured-output
        // repair is scoped to LlmPlanGeneratorService and CragEvaluator, two different CQRS commands
        // this handler never calls; StructuredOutputInvokerTests already proves that repair loop in
        // isolation against the same RecordingChatClient/RoleScript fakes. Re-asserting it here
        // through a seam that cannot reach it would pass or fail for reasons unrelated to what it
        // claims to test.
        //
        // The real "must not clobber good output" property THIS seam owns is conversation-cache
        // reuse across turns (IAgentConversationCache.GetOrCreateAsync): the second turn's model
        // response must not leak the first turn's text, and vice versa.
        //
        // Turns deliberately use DIFFERENT user messages. AgentFactory.BuildMiddlewarePipeline
        // (AgentFactory.ChatClient.cs) wraps every chat client in .UseDistributedCache(...) —
        // real, deliberate prompt-caching middleware. Two turns sending byte-identical messages
        // would legitimately cache-hit on the second turn and never reach the chat client at all,
        // which would make this test pass or fail on cache behaviour instead of on conversation
        // continuity. Distinct messages per turn is also how a real multi-turn conversation looks.
        var (provider, log) = BuildPipeline(factory =>
            factory.ForRole(AgentName)
                .Enqueue("First turn response")
                .Enqueue("Second turn response"));
        using var providerDisposal = provider;
        using var scope = provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var first = await mediator.Send(Command(turnNumber: 1, userMessage: "Hello"));
        var second = await mediator.Send(Command(turnNumber: 2, userMessage: "What's next?"));

        first.Success.Should().BeTrue(first.Error);
        second.Success.Should().BeTrue(second.Error);
        first.Response.Should().Contain("First turn response");
        second.Response.Should().Contain("Second turn response");
        second.Response.Should().NotContain("First turn response");
        log.CountFor(AgentName).Should().Be(2, "each of the two turns reaches the chat client exactly once");
    }

    [Fact]
    public async Task TotalFailure_EveryCallThrows_ReturnsFailureNotAnEmptySuccess()
    {
        var (provider, _) = BuildPipeline(factory =>
            factory.ForRole(AgentName).AlwaysThrow(new InvalidOperationException("model unavailable")));
        using var providerDisposal = provider;
        using var scope = provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var result = await mediator.Send(Command());

        // The exact shape #322 asks not to reproduce is an empty response presented as SUCCESS.
        // ExecuteAgentTurnCommandHandler's catch-all deliberately pairs Response = string.Empty with
        // Success = false (see the handler's generic catch block) — that pairing is correct and is
        // what this asserts; an empty Response is only a defect when Success is also true.
        result.Success.Should().BeFalse();
        result.Error.Should().NotBeNullOrWhiteSpace();
        result.Response.Should().BeEmpty();
    }

    [Fact]
    public async Task UnrecognisedRole_FactoryFailsLoudly_TurnFailsRatherThanSilentlyDefaulting()
    {
        var (provider, _) = BuildPipeline(factory =>
            factory.ForRole("some-other-agent").WithDefaultResponse("never reached"));
        using var providerDisposal = provider;
        using var scope = provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        // AgentName has no script and no ForAnyUnscriptedRole() fallback was configured.
        var result = await mediator.Send(Command());

        result.Success.Should().BeFalse();
        result.Error.Should().NotBeNullOrWhiteSpace();
    }
}
