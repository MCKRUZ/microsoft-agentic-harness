using System.Runtime.CompilerServices;
using System.Text.Json;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Agent;
using Domain.AI.Agents;
using Domain.AI.Models;
using Domain.AI.Skills;
using Domain.Common.Telemetry;
using Microsoft.Agents.AI;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;
using OpenTelemetry.Metrics;

namespace Presentation.AgentHub.Tests.Telemetry.Fixtures;

/// <summary>
/// Integration test factory that keeps the real MediatR pipeline active (unlike
/// <see cref="TestWebApplicationFactory"/> which mocks IMediator) so that handler code
/// — including metric emission — executes for real. External dependencies (AI client,
/// observability store, content safety) are replaced with lightweight stubs.
/// </summary>
public class MetricsIntegrationFactory : TestWebApplicationFactory
{
    /// <inheritdoc/>
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        // Replace the standalone MeterProvider with one that listens to our app meter
        // and exposes the Prometheus scrape endpoint.
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<MeterProvider>();
            services.AddOpenTelemetry()
                .WithMetrics(m =>
                {
                    m.AddMeter(AppSourceNames.AgenticHarness);
                    m.AddPrometheusExporter();
                });
        });

        builder.ConfigureTestServices(services =>
        {
            // Auth: use TestAuthHandler so requests pass through [Authorize] endpoints.
            services.AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                    TestAuthHandler.SchemeName, _ => { });

            // CRITICAL: Remove the mocked IMediator added by base TestWebApplicationFactory
            // and restore the real MediatR pipeline so handlers execute and emit metrics.
            services.RemoveAll<MediatR.IMediator>();
            services.AddMediatR(cfg =>
            {
                cfg.RegisterServicesFromAssemblyContaining<Application.Core.CQRS.Agents.RunConversation.RunConversationCommand>();
                cfg.RegisterServicesFromAssemblyContaining<IAgentFactory>();
            });

            // Mock IAgentFactory: returns a canned AIAgent that produces a fixed response.
            var mockAgentFactory = new Mock<IAgentFactory>();
            mockAgentFactory
                .Setup(f => f.CreateAgentFromSkillAsync(
                    It.IsAny<string>(),
                    It.IsAny<SkillAgentOptions>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new StubAIAgent("Integration test response."));
            mockAgentFactory
                .Setup(f => f.CreateAgentFromSkillAsync(
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new StubAIAgent("Integration test response."));
            services.RemoveAll<IAgentFactory>();
            services.AddSingleton(mockAgentFactory.Object);

            // Mock IAgentMetadataRegistry: returns null for all lookups (handler
            // falls back to treating AgentName as a skill ID).
            var mockRegistry = new Mock<IAgentMetadataRegistry>();
            mockRegistry
                .Setup(r => r.TryGet(It.IsAny<string>()))
                .Returns((AgentDefinition?)null);
            mockRegistry
                .Setup(r => r.GetAll())
                .Returns(Array.Empty<AgentDefinition>());
            services.RemoveAll<IAgentMetadataRegistry>();
            services.AddSingleton(mockRegistry.Object);

            // Mock IObservabilityStore: no-op persistence.
            var mockObsStore = new Mock<IObservabilityStore>();
            mockObsStore
                .Setup(s => s.StartSessionAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Guid.NewGuid());
            mockObsStore
                .Setup(s => s.RecordMessageAsync(
                    It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>(),
                    It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(),
                    It.IsAny<int>(), It.IsAny<int>(), It.IsAny<decimal>(), It.IsAny<decimal>(),
                    It.IsAny<string[]?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Guid.NewGuid());
            services.RemoveAll<IObservabilityStore>();
            services.AddSingleton(mockObsStore.Object);

            // Mock ITextContentSafetyService: always passes (not blocked).
            var mockSafety = new Mock<ITextContentSafetyService>();
            mockSafety
                .Setup(s => s.ScreenAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ContentSafetyResult(false, null, null));
            services.RemoveAll<ITextContentSafetyService>();
            services.AddSingleton(mockSafety.Object);
        });
    }
}

/// <summary>
/// Minimal <see cref="AIAgent"/> implementation for integration tests that returns
/// a fixed response without calling any external AI service.
/// </summary>
internal sealed class StubAIAgent : AIAgent
{
    private readonly string _responseText;

    public StubAIAgent(string responseText) => _responseText = responseText;

    protected override Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(new AgentResponse(new ChatMessage(ChatRole.Assistant, _responseText)));
    }

    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        yield return new AgentResponseUpdate(ChatRole.Assistant, _responseText);
    }

    protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult<AgentSession>(new StubAgentSession());

    protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
        AgentSession session, JsonSerializerOptions? options, CancellationToken cancellationToken)
        => ValueTask.FromResult(JsonDocument.Parse("{}").RootElement);

    protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
        JsonElement serializedState, JsonSerializerOptions? options, CancellationToken cancellationToken)
        => ValueTask.FromResult<AgentSession>(new StubAgentSession());
}

/// <summary>Stub session for <see cref="StubAIAgent"/>.</summary>
internal sealed class StubAgentSession : AgentSession { }
