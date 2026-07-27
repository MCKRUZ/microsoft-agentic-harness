using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Application.AI.Common.Interfaces.Routing;
using Application.Core.Workflows.KnowledgeGraph;
using Domain.AI.RAG.Models;
using Domain.AI.Routing.Enums;
using Domain.AI.Routing.Models;
using Domain.Common.Config;
using FluentAssertions;
using Infrastructure.AI.KnowledgeGraph;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.KnowledgeGraph;

/// <summary>
/// End-to-end proof that the "kg-ingestion" workflow (the enrichment stage consumed by
/// <c>IngestDocumentCommandHandler</c>) writes extracted entities through the DECORATED
/// <c>IKnowledgeGraphStore</c> chain, not the raw backend: with tenant isolation and
/// compliance enabled on the in_memory provider, nodes persisted by
/// <c>StoreGraphExecutor</c> must arrive tenant-stamped from the caller's ambient
/// <c>IKnowledgeScope</c>, provenance-stamped for the ingestion pipeline, and — per the
/// writer-authoritative ownership rule — unowned (shared within the tenant).
/// </summary>
public sealed class KgIngestionWorkflowDecoratedStoreTests
{
    private const string ExtractionJson =
        """
        {"entities":[{"name":"Contoso","type":"organization"},{"name":"Fabrikam","type":"organization"}],
         "relationships":[{"source":"Contoso","predicate":"acquired","target":"Fabrikam"}]}
        """;

    [Fact]
    public async Task Run_WithAmbientTenantScope_StoresTenantStampedNodesThroughDecoratedChain()
    {
        // Arrange: real KnowledgeGraph DI (in_memory provider, tenant isolation + compliance on),
        // a mocked model router whose chat client returns a fixed extraction payload.
        var appConfig = new AppConfig();
        appConfig.AI.Rag.GraphRag.GraphProvider = "in_memory";
        appConfig.AI.Rag.GraphRag.MultiTenantIsolation = true;
        appConfig.AI.Rag.GraphRag.ComplianceEnabled = true;
        appConfig.AI.Rag.GraphRag.ProvenanceEnabled = true;

        var monitor = new Mock<IOptionsMonitor<AppConfig>>();
        monitor.SetupGet(m => m.CurrentValue).Returns(appConfig);

        var chatClient = new Mock<IChatClient>();
        chatClient
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, ExtractionJson)));

        var modelRouter = new Mock<IModelRouter>();
        modelRouter
            .Setup(r => r.RouteOperationAsync("graph_entity_extraction", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ModelRoutingDecision
            {
                SelectedTier = new ModelTier
                {
                    Name = "economy",
                    ClientType = default,
                    DeploymentName = "test-deployment"
                },
                Client = chatClient.Object,
                Complexity = TaskComplexity.Simple,
                Source = ClassificationSource.Heuristic,
                Confidence = 1.0
            });

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(monitor.Object);
        services.AddSingleton(modelRouter.Object);
        services.AddKnowledgeGraphDependencies(appConfig);

        await using var provider = services.BuildServiceProvider();
        var workflow = KgIngestionWorkflow.Build(provider);

        var chunk = new DocumentChunk
        {
            Id = "doc-1_chunk_0",
            DocumentId = "doc-1",
            SectionPath = "Root",
            Content = "Contoso acquired Fabrikam in 2025.",
            Tokens = 8,
            Metadata = new ChunkMetadata
            {
                SourceUri = new Uri("file:///docs/report.md"),
                CreatedAt = DateTimeOffset.UtcNow,
            },
        };

        // Act: run the workflow inside an ambient request scope carrying the caller's
        // knowledge scope — exactly how the ingestion handler runs it under the MediatR
        // AmbientRequestScopeBehavior.
        var knowledgeScope = Mock.Of<IKnowledgeScope>(s =>
            s.UserId == "user-1" && s.TenantId == "tenant-1");
        var scopeServices = new ServiceCollection();
        scopeServices.AddSingleton(knowledgeScope);
        await using var scopeProvider = scopeServices.BuildServiceProvider();

        var ambient = provider.GetRequiredService<IAmbientRequestScope>();
        KgIngestionResult? outcome;
        using (ambient.BeginScope(scopeProvider))
        {
            await using var run = await InProcessExecution.RunAsync(
                workflow, new KgIngestionInput([chunk]), cancellationToken: CancellationToken.None);

            outcome = run.NewEvents
                .OfType<WorkflowOutputEvent>()
                .Select(e => e.Is<KgIngestionResult>(out var r) ? r : null)
                .LastOrDefault(r => r is not null);
        }

        // Assert: the workflow reported stored entities...
        outcome.Should().NotBeNull();
        outcome!.NodesStored.Should().Be(2);
        outcome.EdgesStored.Should().Be(1);

        // ...and the RAW in_memory backend (beneath the decorators) holds nodes that only the
        // decorated chain could have produced: tenant-stamped by ComplianceAwareGraphStore from
        // the ambient scope, provenance-stamped for the ingestion pipeline, and unowned
        // (ownership is writer-authoritative; corpus entities stay shared within the tenant).
        var rawBackend = provider.GetRequiredKeyedService<IKnowledgeGraphStore>("in_memory");
        var nodes = await rawBackend.GetAllNodesAsync(CancellationToken.None);

        nodes.Should().HaveCount(2);
        nodes.Should().OnlyContain(n => n.TenantId == "tenant-1",
            "ComplianceAwareGraphStore stamps the ambient scope's tenant on write");
        nodes.Should().OnlyContain(n => n.OwnerId == null,
            "corpus entities are shared within the tenant — ownership is never defaulted");
        nodes.Should().OnlyContain(n => n.Provenance != null,
            "StampProvenanceExecutor stamps every node before storage");
        nodes.Select(n => n.Name).Should().BeEquivalentTo("Contoso", "Fabrikam");
    }
}
