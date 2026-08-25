using Application.AI.Common.Factories;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Skills;
using Application.AI.Common.Services.Skills;
using Application.AI.Common.Tests.Fakes;
using Domain.AI.Agents;
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
/// Proves <see cref="ISecretRedactor"/> is wired to
/// <see cref="Application.AI.Common.Middleware.ToolDiagnosticsMiddleware"/> on the live agent turn
/// path. Pre-fix, <c>AgentFactory.BuildMiddlewarePipeline</c> constructed the
/// middleware with <c>redactor: null</c>, so <c>ToolPayloadRedactor.Redact</c> short-circuited and
/// every tool payload the middleware captured for the observability store went out unscrubbed,
/// regardless of whether a real <see cref="ISecretRedactor"/> was registered in the container.
/// </summary>
public sealed class AgentFactoryRedactorWiringTests
{
    private static AgentFactory CreateFactory(FakeChatClient innerClient, ISecretRedactor redactor)
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

        var contextFactory = new Mock<AgentExecutionContextFactory>(
            NullLogger<AgentExecutionContextFactory>.Instance,
            monitor,
            new ServiceCollection().BuildServiceProvider(),
            NullLoggerFactory.Instance,
            null!, null!, new UnsandboxedSkillFileReader(), null!, null!, null!, null!, null!);

        var services = new ServiceCollection();
        services.AddSingleton(redactor);
        var serviceProvider = services.BuildServiceProvider();

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

    private static AgentExecutionContext AgentContext() => new()
    {
        Name = "redactor-canary-agent",
        Instruction = "You are a test agent.",
        AIAgentFrameworkType = AIAgentFrameworkClientType.AzureOpenAI
    };

    [Fact]
    public async Task CreateAgentAsync_TurnCarriesAPriorToolResult_RegisteredRedactorSeesItsRawContent()
    {
        // A prior tool result already sitting in conversation history is exactly what
        // ToolDiagnosticsMiddleware.AppendFunctionResultTracesAsync scans on every call — the same
        // seam a resumed conversation's replayed tool history will flow through. If the redactor
        // isn't wired, this content reaches the observability store unscrubbed.
        const string CanarySecret = "api_key=canary-secret-should-never-be-persisted-raw";

        var innerClient = new FakeChatClient().WithDefaultResponse("ok");
        var redactor = new Mock<ISecretRedactor>();
        redactor.Setup(r => r.Redact(It.IsAny<string>())).Returns("[REDACTED]");

        var factory = CreateFactory(innerClient, redactor.Object);
        var agent = await factory.CreateAgentAsync(AgentContext());

        // A lone Tool-role message is all AppendFunctionResultTracesAsync scans for — matching the
        // convention ToolDiagnosticsMiddlewareTests.cs already uses to exercise the same code path.
        var history = new List<ChatMessage>
        {
            new(ChatRole.Tool, [new FunctionResultContent("call-1", CanarySecret)]),
        };

        await agent.RunAsync(history);

        // Pre-fix (redactor: null), ToolPayloadRedactor.Redact short-circuits and this mock is never
        // invoked at all. Post-fix, the middleware must hand the raw payload to the registered
        // redactor before it is ever recorded.
        redactor.Verify(
            r => r.Redact(It.Is<string>(s => s.Contains(CanarySecret))),
            Times.AtLeastOnce,
            "AgentFactory must wire the registered ISecretRedactor into ToolDiagnosticsMiddleware " +
            "so tool payloads are scrubbed before they reach the observability store");
    }
}
