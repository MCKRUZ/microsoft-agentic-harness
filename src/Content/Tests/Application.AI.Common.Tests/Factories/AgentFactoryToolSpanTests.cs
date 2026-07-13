using System.Diagnostics;
using Application.AI.Common.Factories;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Skills;
using Application.AI.Common.Services.Skills;
using Application.AI.Common.Tests.Fakes;
using Domain.AI.Agents;
using Domain.AI.Telemetry.Conventions;
using Domain.Common.Config;
using Domain.Common.Config.AI;
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
/// Guards the OpenTelemetry composition invariant that makes per-tool telemetry observable.
/// <para>
/// Microsoft.Extensions.AI's <c>FunctionInvokingChatClient</c> only emits an
/// <c>execute_tool</c> span when it can resolve an <see cref="ActivitySource"/> from the client
/// composed <em>below</em> it (<c>innerClient.GetService&lt;ActivitySource&gt;()</c>), which is
/// exposed by <c>.UseOpenTelemetry()</c>. If OpenTelemetry is composed <em>above</em> function
/// invocation, that lookup returns <see langword="null"/> and no <c>execute_tool</c> span is ever
/// created — silently starving the tool-effectiveness / tool-usefulness / causal-attribution span
/// processors and their dashboard tiles.
/// </para>
/// <para>
/// This test drives a real <see cref="AgentFactory"/> pipeline (the same
/// <c>BuildMiddlewarePipeline</c> production uses) over a deterministic <see cref="FakeChatClient"/>
/// tool round-trip and asserts the <c>execute_tool</c> span is emitted. It fails loudly if the
/// OpenTelemetry / function-invocation ordering regresses.
/// </para>
/// </summary>
public sealed class AgentFactoryToolSpanTests
{
    // The Microsoft.Extensions.AI OpenTelemetry source that FunctionInvokingChatClient emits
    // execute_tool spans on (matched loosely so it survives the SDK dropping the Experimental
    // prefix at GA).
    private const string MeaiSourceFragment = "Microsoft.Extensions.AI";

    private static AgentFactory CreateFactory(FakeChatClient innerClient)
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
            .ReturnsAsync(innerClient);

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

        return new AgentFactory(
            NullLogger<AgentFactory>.Instance,
            monitor,
            new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())),
            NullLoggerFactory.Instance,
            // The AgentExecutionContext overload of CreateAgentAsync never touches the context
            // factory (only the skills overload does), and the constructor stores it without a
            // guard — so this path is exercised fully with a null factory.
            agentContextFactory: null!,
            Mock.Of<ISkillMetadataRegistry>(),
            chatClientFactory.Object,
            new ServiceCollection().BuildServiceProvider(),
            new InMemorySkillCompletionTracker());
    }

    [Fact]
    public async Task AgentTurnInvokingATool_EmitsExecuteToolSpan()
    {
        var toolOperations = new List<string>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name.Contains(MeaiSourceFragment, StringComparison.Ordinal),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (activity.GetTagItem(ToolConventions.GenAiOperationName) is string operation)
                    toolOperations.Add(operation);
            }
        };
        ActivitySource.AddActivityListener(listener);

        var tool = AIFunctionFactory.Create(() => "the-result", "my_tool", "does a thing");

        var innerClient = new FakeChatClient()
            .EnqueueResponseWithToolCall("my_tool", "call-1")
            .EnqueueResponse("done after tool");

        var factory = CreateFactory(innerClient);

        var agent = await factory.CreateAgentAsync(new AgentExecutionContext
        {
            Name = "tool-span-agent",
            Instruction = "You are a test agent.",
            AIAgentFrameworkType = AIAgentFrameworkClientType.AzureOpenAI,
            Tools = new List<AITool> { tool }
        });

        await agent.RunAsync("please use the tool");

        toolOperations.Should().Contain(
            ToolConventions.ExecuteToolOperation,
            "FunctionInvokingChatClient must emit an execute_tool span, which requires "
            + ".UseOpenTelemetry() to be composed below .UseFunctionInvocation() in "
            + "AgentFactory.BuildMiddlewarePipeline");
    }
}
