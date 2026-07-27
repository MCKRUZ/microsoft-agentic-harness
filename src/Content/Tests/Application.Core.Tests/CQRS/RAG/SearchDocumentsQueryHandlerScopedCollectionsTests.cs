using Application.AI.Common.Interfaces.KnowledgeGraph;
using Application.AI.Common.Interfaces.RAG;
using Application.Core.CQRS.RAG.SearchDocuments;
using Domain.AI.RAG.Enums;
using Domain.AI.RAG.Models;
using Domain.Common.Config;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Application.Core.Tests.CQRS.RAG;

/// <summary>
/// Verifies that <see cref="SearchDocumentsQueryHandler"/> resolves the collection it
/// searches server-side when <c>ScopedCollections</c> is enabled: the ambient tenant's
/// derived collection is passed to the orchestrator, no-identity callers search the
/// global/default collection, and the flag-off path passes the caller's collection
/// through byte-for-byte.
/// </summary>
public sealed class SearchDocumentsQueryHandlerScopedCollectionsTests
{
    private readonly Mock<IRagOrchestrator> _orchestrator = new();
    private readonly Mock<IKnowledgeScope> _scope = new();

    public SearchDocumentsQueryHandlerScopedCollectionsTests()
    {
        _orchestrator
            .Setup(o => o.SearchAsync(
                It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<string?>(),
                It.IsAny<RetrievalStrategy?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RagAssembledContext
            {
                AssembledText = "assembled",
                TotalTokens = 10,
                WasTruncated = false,
            });
    }

    private SearchDocumentsQueryHandler CreateHandler(bool scopedCollections)
    {
        var appConfig = new AppConfig();
        appConfig.AI.Rag.ScopedCollections.Enabled = scopedCollections;

        var monitor = new Mock<IOptionsMonitor<AppConfig>>();
        monitor.SetupGet(m => m.CurrentValue).Returns(appConfig);

        return new SearchDocumentsQueryHandler(
            _orchestrator.Object,
            _scope.Object,
            monitor.Object,
            NullLogger<SearchDocumentsQueryHandler>.Instance);
    }

    [Fact]
    public async Task Handle_ScopedCollectionsEnabled_PassesDerivedCollectionToOrchestrator()
    {
        _scope.SetupGet(s => s.TenantId).Returns("tenant-a");
        var expected = ScopedCollectionName.DeriveForTenant("tenant-a");

        var result = await CreateHandler(scopedCollections: true)
            .Handle(new SearchDocumentsQuery { Query = "find things" }, CancellationToken.None);

        result.Success.Should().BeTrue();
        _orchestrator.Verify(o => o.SearchAsync(
            "find things", It.IsAny<int?>(), expected,
            It.IsAny<RetrievalStrategy?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_ScopedCollectionsEnabledWithoutTenant_SearchesGlobalDefaultCollection()
    {
        var result = await CreateHandler(scopedCollections: true)
            .Handle(new SearchDocumentsQuery { Query = "find things" }, CancellationToken.None);

        result.Success.Should().BeTrue();
        _orchestrator.Verify(o => o.SearchAsync(
            "find things", It.IsAny<int?>(), null,
            It.IsAny<RetrievalStrategy?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_ScopedCollectionsDisabled_PassesCallerSuppliedCollectionUnchanged()
    {
        _scope.SetupGet(s => s.TenantId).Returns("tenant-a");

        var result = await CreateHandler(scopedCollections: false)
            .Handle(
                new SearchDocumentsQuery { Query = "find things", CollectionName = "corpus-a" },
                CancellationToken.None);

        result.Success.Should().BeTrue();
        _orchestrator.Verify(o => o.SearchAsync(
            "find things", It.IsAny<int?>(), "corpus-a",
            It.IsAny<RetrievalStrategy?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_CrossTenantScopes_DeriveDistinctCollections()
    {
        _scope.SetupGet(s => s.TenantId).Returns("tenant-a");
        await CreateHandler(scopedCollections: true)
            .Handle(new SearchDocumentsQuery { Query = "q" }, CancellationToken.None);

        _scope.SetupGet(s => s.TenantId).Returns("tenant-b");
        await CreateHandler(scopedCollections: true)
            .Handle(new SearchDocumentsQuery { Query = "q" }, CancellationToken.None);

        var collections = _orchestrator.Invocations
            .Where(i => i.Method.Name == nameof(IRagOrchestrator.SearchAsync))
            .Select(i => (string?)i.Arguments[2])
            .ToList();

        collections.Should().HaveCount(2);
        collections.Should().OnlyHaveUniqueItems(
            "two tenants must never resolve to the same collection");
    }
}
