using Application.AI.Common.Interfaces;
using Application.AI.Common.Middleware;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using FluentAssertions;
using Infrastructure.AI.Factories;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Connectivity;

/// <summary>
/// Integration tests that verify live connectivity to <c>api.anthropic.com</c> directly, with no
/// Azure AI Foundry relay (issue #592) — the issue's own acceptance criterion: a cached turn
/// reports non-zero cached tokens on the second call.
/// </summary>
/// <remarks>
/// Opt-in via user secrets for the agentic-harness-console-ui project (test-only keys, distinct
/// from <c>AppConfig:AI:AgentFramework:*</c> so this never collides with the Azure Foundry
/// connectivity tests' own use of that section):
/// <code>
/// dotnet user-secrets set "Test:AnthropicDirect:ApiKey" "sk-ant-..."          --project src/Content/Presentation/Presentation.ConsoleUI
/// dotnet user-secrets set "Test:AnthropicDirect:Model" "claude-opus-4-8"      --project src/Content/Presentation/Presentation.ConsoleUI
/// </code>
/// When either secret is missing, the test is reported as <c>Skipped</c> rather than
/// <c>Failed</c> — so a fresh clone running <c>dotnet test</c> never sees red without good cause.
/// Exercises the real harness stack end to end (<see cref="ChatClientFactory"/> →
/// <see cref="ObservabilityMiddleware"/>), not just a raw HTTP call, so a pass proves the whole
/// path this issue built — native caching wiring and the cache-token key-mismatch fix together —
/// not merely that the API key is valid.
/// </remarks>
[Trait("Category", "Integration")]
public class AnthropicDirectConnectivityTests
{
    private readonly string? _apiKey;
    private readonly string? _model;

    public AnthropicDirectConnectivityTests()
    {
        var config = new ConfigurationBuilder()
            .AddUserSecrets("agentic-harness-console-ui")
            .Build();

        _apiKey = config["Test:AnthropicDirect:ApiKey"];
        _model = config["Test:AnthropicDirect:Model"];
    }

    [SkippableFact]
    public async Task CachedTurn_SecondCall_ReportsNonZeroCachedTokens()
    {
        Skip.If(
            string.IsNullOrWhiteSpace(_apiKey) || string.IsNullOrWhiteSpace(_model),
            "Direct Anthropic credentials not configured in user secrets for agentic-harness-console-ui. " +
            "Set Test:AnthropicDirect:ApiKey and Test:AnthropicDirect:Model to enable.");

        var appConfig = new AppConfig
        {
            AI = new AIConfig
            {
                AgentFramework = new AgentFrameworkConfig
                {
                    ApiKey = _apiKey,
                    ClientType = AIAgentFrameworkClientType.AnthropicDirect,
                    EnablePromptCaching = true,
                }
            }
        };
        var appConfigMonitor = Mock.Of<IOptionsMonitor<AppConfig>>(o => o.CurrentValue == appConfig);

        var services = new ServiceCollection();
        services.AddSingleton(appConfigMonitor);
        using var factory = new ChatClientFactory(appConfigMonitor, services.BuildServiceProvider());

        var chatClient = await factory.GetChatClientAsync(AIAgentFrameworkClientType.AnthropicDirect, _model!);

        var usageCapture = new Mock<ILlmUsageCapture>();
        var observed = new ObservabilityMiddleware(chatClient, NullLogger<ObservabilityMiddleware>.Instance, usageCapture.Object);

        // Long enough to clear Claude's minimum cacheable-block size — a short system prompt never
        // gets cached regardless of whether PromptCaching is requested.
        var longSystemPrompt = string.Concat(Enumerable.Repeat(
            "You are a helpful assistant for automated testing purposes. ", 100));
        var options = new ChatOptions { Instructions = longSystemPrompt };
        var messages = new[] { new ChatMessage(ChatRole.User, "Say hello in one word.") };

        await observed.GetResponseAsync(messages, options);
        await observed.GetResponseAsync(messages, options);

        // The second call should read from the cache the first call wrote. Cache reads are the
        // acceptance criterion's own wording; asserting on the mock's last recorded call is enough
        // because the fifth Record argument order is (input, output, cacheRead, cacheWrite, model).
        usageCapture.Verify(
            c => c.Record(
                It.IsAny<int>(), It.IsAny<int>(), It.Is<int>(cacheRead => cacheRead > 0), It.IsAny<int>(), It.IsAny<string?>()),
            Times.AtLeastOnce,
            "the second call should report a non-zero cache read once Anthropic has cached the first call's system prompt");
    }
}
