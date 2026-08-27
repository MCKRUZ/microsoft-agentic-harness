using Application.AI.Common.Factories;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Skills;
using Application.AI.Common.Interfaces.Traces;
using Application.AI.Common.Services.Skills;
using Application.AI.Common.Services.Tools;
using Application.AI.Common.Tests.Fakes;
using Domain.AI.Agents;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Domain.Common.MetaHarness;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.Factories;

/// <summary>
/// Proves the execution-trace subsystem is wired to the live agent path (#505).
/// </summary>
/// <remarks>
/// <para>
/// Pre-fix, two producers stashed a per-run <see cref="ITraceWriter"/> into
/// <c>AgentExecutionContext.AdditionalProperties["__traceWriter"]</c> —
/// <see cref="AgentExecutionContextFactory"/> for production turns and
/// <c>AgentEvaluationService</c> for meta-harness evaluation runs — and <strong>nothing read
/// it</strong>. <see cref="AgentFactory"/> built <c>ToolDiagnosticsMiddleware</c> without the
/// optional <c>traceWriter</c> argument, so its entire trace-writing branch never executed
/// outside tests that construct the middleware directly with a mock. Every evaluation run
/// therefore produced a trace directory with run metadata and no tool records in
/// <c>traces.jsonl</c>.
/// </para>
/// <para>
/// The defect was invisible to a registration scan because <see cref="ITraceWriter"/> is not a
/// DI service and must not become one: it is created per run by
/// <see cref="IExecutionTraceStore.StartRunAsync"/> and carries that run's scope. The
/// producer/consumer channel is the additional-properties stash, exactly as for the resilient
/// chat client (<see cref="AgentFactoryResilienceWiringTests"/>) and the skill prerequisite map.
/// A dead stash is the same defect shape either way — the check that catches it is a test that
/// drives the real factory and asserts the consumer received the value.
/// </para>
/// <para>
/// <strong>The line these tests are bound to:</strong> the <c>traceWriter:</c> argument in
/// <c>AgentFactory.BuildMiddlewarePipeline</c>'s <c>ToolDiagnosticsMiddleware</c> construction.
/// Delete it and <see cref="CreateAgentAsync_TraceWriterInContext_ToolResultsReachTheWriter"/>
/// fails.
/// </para>
/// </remarks>
public sealed class AgentFactoryTraceWriterWiringTests
{
    /// <summary>
    /// Key the producers use to stash the run's writer. Bound to the production constant so the
    /// producer/consumer contract cannot silently drift.
    /// </summary>
    private const string TraceWriterKey = ITraceWriter.AdditionalPropertiesKey;

    /// <summary>
    /// Builds the factory. <paramref name="withRedactor"/> defaults to true because every real
    /// composition registers one (Infrastructure.AI's does unconditionally); the fail-closed test
    /// passes false to exercise the host that has not.
    /// </summary>
    private static AgentFactory CreateAgentFactory(FakeChatClient rawClient, bool withRedactor = true)
    {
        var chatClientFactory = new Mock<IChatClientFactory>();
        chatClientFactory
            .Setup(f => f.IsAvailable(It.IsAny<AIAgentFrameworkClientType>()))
            .Returns(true);
        chatClientFactory
            .Setup(f => f.GetChatClientAsync(
                It.IsAny<AIAgentFrameworkClientType>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(rawClient);

        var appConfig = new AppConfig
        {
            AI = new AIConfig
            {
                AgentFramework = new AgentFrameworkConfig
                {
                    DefaultDeployment = "gpt-4o",
                    ClientType = AIAgentFrameworkClientType.AzureOpenAI
                }
            }
        };
        var monitor = Mock.Of<IOptionsMonitor<AppConfig>>(m => m.CurrentValue == appConfig);

        var services = new ServiceCollection();
        if (withRedactor)
        {
            var redactor = new Mock<ISecretRedactor>();
            redactor.Setup(r => r.Redact(It.IsAny<string>())).Returns((string? text) => text);
            services.AddSingleton(redactor.Object);
        }
        var serviceProvider = services.BuildServiceProvider();

        var contextFactory = new Mock<AgentExecutionContextFactory>(
            NullLogger<AgentExecutionContextFactory>.Instance,
            monitor,
            new ServiceCollection().BuildServiceProvider(),
            NullLoggerFactory.Instance,
            null!, null!, new UnsandboxedSkillFileReader(), null!, null!, null!, null!, null!);

        return new AgentFactory(
            NullLogger<AgentFactory>.Instance,
            monitor,
            new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())),
            NullLoggerFactory.Instance,
            contextFactory.Object,
            Mock.Of<ISkillMetadataRegistry>(),
            chatClientFactory.Object,
            serviceProvider,
            new InMemorySkillCompletionTracker());
    }

    /// <summary>
    /// A turn whose inbound history carries a completed tool call, which is the shape
    /// <c>ToolDiagnosticsMiddleware</c> scans — <c>FunctionInvokingChatClient</c> appends a
    /// result to the <em>next</em> round's inbound messages, not to this middleware's own
    /// outbound response.
    /// </summary>
    private static List<ChatMessage> TurnCarryingAToolResult() =>
    [
        new(ChatRole.User, "summarise the file"),
        new(ChatRole.Tool, [new FunctionResultContent("call-1", "file contents here")])
    ];

    [Fact]
    public async Task CreateAgentAsync_TraceWriterInContext_ToolResultsReachTheWriter()
    {
        // The canary records every trace append. Pre-fix it records ZERO, because the stashed
        // writer was never handed to the middleware that would have called it.
        var appended = new List<ExecutionTraceRecord>();
        var writer = new Mock<ITraceWriter>();
        // Scope is read to stamp the record's ExecutionRunId before the append. A mock that leaves
        // it null throws inside the middleware's own try/catch, which swallows it — the test would
        // then fail identically whether the wiring worked or not, and prove nothing either way.
        writer.SetupGet(w => w.Scope).Returns(TraceScope.ForExecution(Guid.NewGuid()));
        writer
            .Setup(w => w.AppendTraceAsync(It.IsAny<ExecutionTraceRecord>(), It.IsAny<CancellationToken>()))
            .Callback((ExecutionTraceRecord r, CancellationToken _) => appended.Add(r))
            .Returns(Task.CompletedTask);

        var factory = CreateAgentFactory(new FakeChatClient().WithDefaultResponse("done"));

        var context = new AgentExecutionContext
        {
            Name = "trace-canary-agent",
            Instruction = "You are a test agent.",
            AIAgentFrameworkType = AIAgentFrameworkClientType.AzureOpenAI,
            AdditionalProperties = new Dictionary<string, object>
            {
                [TraceWriterKey] = writer.Object
            }
        };

        var agent = await factory.CreateAgentAsync(context);
        await agent.RunAsync(TurnCarryingAToolResult());

        appended.Should().NotBeEmpty(
            "a run that stashes a trace writer must have its tool results recorded to that "
            + "writer — otherwise the run directory is created, the manifest is written, and "
            + "traces.jsonl stays empty for every tool the agent actually called");
        appended.Should().Contain(r => r.TurnId == "call-1",
            "the trace record must identify the tool call it came from");
    }

    [Fact]
    public async Task CreateAgentAsync_TraceWriterButNoRedactorRegistered_DoesNotTrace()
    {
        // Fail closed. With no ISecretRedactor registered, ToolPayloadRedactor returns tool
        // payloads untouched — a documented degraded mode that reached only an in-process capture
        // until the writer was wired up. Now the same mode would write cleartext tool output to a
        // durable file, which is a different exposure class than memory and not something a loud
        // log line answers. Dropping tracing is the cheap side of that trade.
        //
        // The factory in this fixture registers no ISecretRedactor, which is what makes this the
        // default path here rather than a contrived one.
        var writer = new Mock<ITraceWriter>();
        writer.SetupGet(w => w.Scope).Returns(TraceScope.ForExecution(Guid.NewGuid()));
        writer
            .Setup(w => w.AppendTraceAsync(It.IsAny<ExecutionTraceRecord>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var factory = CreateAgentFactory(
            new FakeChatClient().WithDefaultResponse("done"), withRedactor: false);

        var context = new AgentExecutionContext
        {
            Name = "unredacted-agent",
            Instruction = "You are a test agent.",
            AIAgentFrameworkType = AIAgentFrameworkClientType.AzureOpenAI,
            AdditionalProperties = new Dictionary<string, object>
            {
                [TraceWriterKey] = writer.Object
            }
        };

        var agent = await factory.CreateAgentAsync(context);
        await agent.RunAsync(TurnCarryingAToolResult());

        writer.Verify(
            w => w.AppendTraceAsync(It.IsAny<ExecutionTraceRecord>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "unredacted tool output must not reach disk just because tracing was switched on");
    }

    [Fact]
    public async Task CreateAgentAsync_NoTraceWriterInContext_TurnStillSucceeds()
    {
        // Tracing is optional: a context that stashes no writer is the shipped default for a
        // host with no meta-harness run in flight, and must behave exactly as before.
        var rawClient = new FakeChatClient().WithDefaultResponse("done");
        var factory = CreateAgentFactory(rawClient);

        var context = new AgentExecutionContext
        {
            Name = "plain-agent",
            Instruction = "You are a test agent.",
            AIAgentFrameworkType = AIAgentFrameworkClientType.AzureOpenAI,
            AdditionalProperties = new Dictionary<string, object>()
        };

        var agent = await factory.CreateAgentAsync(context);
        var response = await agent.RunAsync(TurnCarryingAToolResult());

        response.Text.Should().Contain("done");
        rawClient.RequestHistory.Should().NotBeEmpty();
    }

    [Fact]
    public async Task CreateAgentAsync_StashedValueIsNotATraceWriter_TurnStillSucceeds()
    {
        // The stash is an untyped object dictionary, so a wrong-typed value must be ignored
        // rather than crash the turn — the same defensive read ResolveStashedResilientClient does.
        var rawClient = new FakeChatClient().WithDefaultResponse("done");
        var factory = CreateAgentFactory(rawClient);

        var context = new AgentExecutionContext
        {
            Name = "wrong-type-agent",
            Instruction = "You are a test agent.",
            AIAgentFrameworkType = AIAgentFrameworkClientType.AzureOpenAI,
            AdditionalProperties = new Dictionary<string, object>
            {
                [TraceWriterKey] = "not a writer"
            }
        };

        var agent = await factory.CreateAgentAsync(context);
        var response = await agent.RunAsync(TurnCarryingAToolResult());

        response.Text.Should().Contain("done");
    }
}
