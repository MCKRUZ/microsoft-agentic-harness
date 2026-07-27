using Application.Core.Validation;
using Domain.Common.Config.AI.RAG;
using FluentAssertions;
using Xunit;

namespace Application.Core.Tests.Validation;

/// <summary>
/// Verifies the cross-section coherence rules of <see cref="RagConfigValidator"/>: an enabled
/// graph database must name a registered backend provider, and corpus-graph indexing on ingest
/// must have a backend to write into. Class defaults must always pass so every host keeps
/// booting unchanged.
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
}
