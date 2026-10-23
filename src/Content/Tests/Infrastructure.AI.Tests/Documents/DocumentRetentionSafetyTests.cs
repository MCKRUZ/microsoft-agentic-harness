using Application.AI.Common.Interfaces.RAG;
using Domain.Common.Config;
using FluentAssertions;
using Infrastructure.AI.Documents;
using Infrastructure.AI.Persistence;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Documents;

/// <summary>
/// The document age limit never runs over stores other processes can write while the load record is this process's own
/// file (#901): a host whose record missed another host's recent load would delete that document for everyone.
/// </summary>
public sealed class DocumentRetentionSafetyTests
{
    private static readonly EfCoreDocumentLoadLedger LocalLedger = new(
        Mock.Of<IDbContextFactory<DocumentLoadDbContext>>(),
        new NoSchema(),
        TimeProvider.System);

    private static AppConfig Config(bool enabled, string vectors, string graph, bool graphDatabase = true,
        string corpusGraph = "kuzu", bool sharedByEveryWriter = false)
    {
        var config = new AppConfig();
        config.AI.Rag.Documents.Retention.Enabled = enabled;
        config.AI.Rag.Documents.LoadLedgerSharedByEveryWriter = sharedByEveryWriter;
        config.AI.Rag.VectorStore.Provider = vectors;
        config.AI.Rag.GraphRag.GraphProvider = graph;
        config.AI.Rag.GraphDatabase.Enabled = graphDatabase;
        config.AI.Rag.GraphDatabase.Provider = corpusGraph;
        return config;
    }

    [Theory]
    [InlineData("azure_ai_search", "managed_code", true, "kuzu")]
    [InlineData("faiss", "postgresql", true, "kuzu")]
    [InlineData("faiss", "neo4j", true, "kuzu")]
    [InlineData("faiss", "managed_code", true, "neo4j")]
    public void SharedStores_WithTheLocalLedger_AreRefused(string vectors, string graph, bool graphDatabase, string corpusGraph)
    {
        DocumentRetentionSafety.Refusal(Config(true, vectors, graph, graphDatabase, corpusGraph), LocalLedger)
            .Should().Contain("LoadLedgerSharedByEveryWriter");
    }

    [Theory]
    [InlineData("faiss", "managed_code", true, "kuzu")]
    [InlineData("faiss", "in_memory", false, "neo4j")]
    public void InProcessStores_MayRunTheAgeLimit(string vectors, string graph, bool graphDatabase, string corpusGraph)
    {
        DocumentRetentionSafety.Refusal(Config(true, vectors, graph, graphDatabase, corpusGraph), LocalLedger).Should().BeNull();
    }

    [Fact]
    public void TheAgeLimitOff_IsNeverRefused() =>
        DocumentRetentionSafety.Refusal(Config(false, "azure_ai_search", "postgresql"), LocalLedger).Should().BeNull();

    [Fact]
    public void ADeploymentDeclaringEveryWriterSharesTheFile_MayRunIt() =>
        DocumentRetentionSafety.Refusal(Config(true, "azure_ai_search", "postgresql", sharedByEveryWriter: true), LocalLedger)
            .Should().BeNull();

    [Fact]
    public void AConsumersOwnLedger_MayRunIt() =>
        DocumentRetentionSafety.Refusal(Config(true, "azure_ai_search", "postgresql"), Mock.Of<IDocumentLoadLedger>())
            .Should().BeNull("a registered ledger of the consumer's own is the shared record the refusal asks for");

    /// <summary>A schema initializer that touches no database, for constructing the ledger without one.</summary>
    private sealed class NoSchema : SchemaInitializer<DocumentLoadDbContext>
    {
        public NoSchema() : base(new EmptyFactory())
        {
        }
    }

    private sealed class EmptyFactory : IDbContextFactory<DocumentLoadDbContext>
    {
        public DocumentLoadDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<DocumentLoadDbContext>().UseSqlite("Data Source=:memory:").Options);
    }
}
