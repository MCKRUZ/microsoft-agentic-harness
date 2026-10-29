using Application.Core.Validation;
using Domain.Common.Config.AI.RAG;
using FluentAssertions;
using Xunit;

namespace Application.Core.Tests.Validation;

/// <summary>
/// Verifies the cross-section coherence rules of <see cref="RagConfigValidator"/>: an enabled
/// graph database must name a registered backend provider, and corpus-graph indexing on ingest
/// must have a backend to write into, and the Azure AI Search provider and its endpoint come
/// together (#1012). Class defaults must always pass so every host keeps booting unchanged.
/// </summary>
public sealed class RagConfigValidatorTests
{
    private readonly RagConfigValidator _sut = new();

    [Fact]
    public void Validate_Defaults_Passes()
    {
        var result = _sut.Validate(new RagConfig());

        result.IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("neo4j")]
    [InlineData("in_memory")]
    [InlineData("")]
    public void Validate_BackendEnabledWithUnregisteredProvider_Fails(string provider)
    {
        var config = new RagConfig();
        config.GraphDatabase.Enabled = true;
        config.GraphDatabase.Provider = provider;

        var result = _sut.Validate(config);

        result.IsValid.Should().BeFalse(
            "an enabled backend with no registered IGraphDatabaseBackend used to throw on first " +
            "resolution instead of failing at startup");
        result.Errors.Should().Contain(e => e.PropertyName == "GraphDatabase.Provider");
    }

    [Theory]
    [InlineData("kuzu")]
    [InlineData("KUZU")]
    public void Validate_BackendEnabledWithRegisteredProvider_Passes(string provider)
    {
        var config = new RagConfig();
        config.GraphDatabase.Enabled = true;
        config.GraphDatabase.Provider = provider;

        var result = _sut.Validate(config);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_BackendDisabled_ProviderIsNotConstrained()
    {
        var config = new RagConfig();
        config.GraphDatabase.Enabled = false;
        config.GraphDatabase.Provider = "neo4j";

        var result = _sut.Validate(config);

        result.IsValid.Should().BeTrue("a disabled backend never resolves a provider");
    }

    [Fact]
    public void Validate_IndexOnIngestWithBackendEnabled_Passes()
    {
        var config = new RagConfig();
        config.GraphDatabase.Enabled = true;
        config.GraphRag.IndexOnIngest = true;

        var result = _sut.Validate(config);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_IndexOnIngestWithoutBackend_Fails()
    {
        var config = new RagConfig();
        config.GraphDatabase.Enabled = false;
        config.GraphRag.IndexOnIngest = true;

        var result = _sut.Validate(config);

        result.IsValid.Should().BeFalse(
            "IndexOnIngest without a backend would silently skip graph indexing on every ingest");
        result.Errors.Should().Contain(e => e.PropertyName == "GraphDatabase.Enabled");
    }

    [Fact]
    public void Validate_EnrichKnowledgeGraphOnIngestWithoutBackend_Passes()
    {
        // Knowledge-graph enrichment writes to IKnowledgeGraphStore (registered
        // unconditionally), not to the GraphRag corpus backend — it has no dependency on
        // GraphDatabase.Enabled.
        var config = new RagConfig();
        config.GraphDatabase.Enabled = false;
        config.GraphRag.EnrichKnowledgeGraphOnIngest = true;

        var result = _sut.Validate(config);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_ScopedCollectionsWithAzureSearchProvider_Fails()
    {
        // The Azure stores ignore collection names, so every tenant would silently share one index.
        var config = AzureSearchConfig();
        config.ScopedCollections.Enabled = true;

        var result = _sut.Validate(config);

        result.IsValid.Should().BeFalse(
            "azure_ai_search stores ignore collection names — allowing the combination would leak cross-tenant");
        result.Errors.Should().Contain(e => e.PropertyName == "VectorStore.Provider");
    }

    [Fact]
    public void Validate_ScopedCollectionsWithFaissProvider_Passes()
    {
        var config = new RagConfig();
        config.ScopedCollections.Enabled = true;
        config.VectorStore.Provider = "faiss";

        var result = _sut.Validate(config);

        result.IsValid.Should().BeTrue("FAISS + SQLite FTS5 are both collection-aware");
    }

    [Fact]
    public void Validate_ScopedCollectionsWithAgenticRetrieval_Fails()
    {
        var config = new RagConfig();
        config.ScopedCollections.Enabled = true;
        config.VectorStore.Provider = "faiss";
        config.AgenticRetrieval.Enabled = true;

        var result = _sut.Validate(config);

        result.IsValid.Should().BeFalse(
            "the knowledge-base retriever always queries its one configured knowledge base");
        result.Errors.Should().Contain(e => e.PropertyName == "AgenticRetrieval.Enabled");
    }

    [Fact]
    public void Validate_ScopedCollectionsDisabled_AzureProviderAndAgenticRetrievalUnconstrained()
    {
        var config = AzureSearchConfig();
        config.AgenticRetrieval.Enabled = true;

        var result = _sut.Validate(config);

        result.IsValid.Should().BeTrue("the invariants only bind when ScopedCollections is on");
    }

    [Fact]
    public void Validate_ScopedCollectionsWithIndexOnIngest_Fails()
    {
        var config = new RagConfig();
        config.ScopedCollections.Enabled = true;
        config.VectorStore.Provider = "faiss";
        config.GraphDatabase.Enabled = true;
        config.GraphRag.IndexOnIngest = true;

        var result = _sut.Validate(config);

        result.IsValid.Should().BeFalse(
            "the corpus graph is one shared graph, so scoped ingests would land every tenant's " +
            "chunks in a graph readable via GraphRag and the multi-source 'graph' source");
        result.Errors.Should().Contain(e => e.PropertyName == "GraphRag.IndexOnIngest");
    }

    [Fact]
    public void Validate_ScopedCollectionsWithoutIndexOnIngest_GraphBackendAloneIsAllowed()
    {
        // The graph database backend itself (e.g. for knowledge-graph memory) is fine;
        // only building the shared corpus graph FROM scoped ingests is banned.
        var config = new RagConfig();
        config.ScopedCollections.Enabled = true;
        config.VectorStore.Provider = "faiss";
        config.GraphDatabase.Enabled = true;

        var result = _sut.Validate(config);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Defaults_UseTheInProcessStore_SoAHostThatOmitsTheSectionNeedsNoEndpoint()
    {
        // A host that never mentions the vector store must run on one it actually has (#1012).
        new RagConfig().VectorStore.Provider.Should().Be("faiss");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_AzureSearchWithoutAnEndpoint_Fails(string? endpoint)
    {
        // A lost endpoint (a failed Key Vault reference, an environment override) must stop the host: running on, every
        // erasure would clear a placeholder and read complete while the person's chunks stay in the real index.
        var config = AzureSearchConfig();
        config.VectorStore.Endpoint = endpoint;

        var result = _sut.Validate(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.PropertyName.Should().Be("VectorStore.Endpoint");
    }

    [Fact]
    public void Validate_AzureSearchSpelledDifferently_WithoutAnEndpoint_StillFails()
    {
        var config = AzureSearchConfig();
        config.VectorStore.Provider = "Azure_AI_Search";
        config.VectorStore.Endpoint = null;

        _sut.Validate(config).Errors.Should().Contain(e => e.PropertyName == "VectorStore.Endpoint");
    }

    [Fact]
    public void Validate_AzureSearchWithAnEndpoint_Passes()
    {
        _sut.Validate(AzureSearchConfig()).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("faiss")]
    [InlineData("Azure_AI_Search")]
    [InlineData("a_consumer_store")]
    public void Validate_AnEndpointWithAnotherProvider_Fails(string provider)
    {
        // An endpoint left with the FAISS default (or any other key) means the index at it is never searched or erased:
        // the in-process stores would answer for it and an erasure would read complete.
        var config = AzureSearchConfig();
        config.VectorStore.Provider = provider;

        var result = _sut.Validate(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "VectorStore.Provider");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Validate_AnotherProviderWithoutAnEndpoint_Passes(string? endpoint)
    {
        var config = new RagConfig();
        config.VectorStore.Provider = "a_consumer_store";
        config.VectorStore.Endpoint = endpoint;

        _sut.Validate(config).IsValid.Should().BeTrue("only the Azure AI Search stores read the endpoint");
    }

    private static RagConfig AzureSearchConfig()
    {
        var config = new RagConfig();
        config.VectorStore.Provider = "azure_ai_search";
        config.VectorStore.Endpoint = "https://svc.search.windows.net";
        return config;
    }
}
