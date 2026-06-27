using Azure;
using Azure.Search.Documents.KnowledgeBases;
using Azure.Search.Documents.KnowledgeBases.Models;
using Domain.Common.Config;
using FluentAssertions;
using Infrastructure.AI.RAG.Retrieval;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.RAG.Tests.Retrieval;

/// <summary>
/// Tests for <see cref="AzureKnowledgeBaseRetriever"/>. Coverage targets the grounding-payload
/// parser (the real risk surface — mapping the knowledge base's JSON response to
/// <c>RetrievalResult</c>) directly via the <c>internal</c> seam, plus the fail-soft path when
/// the backend is enabled but not configured. The thin client round-trip is intentionally not
/// mocked; the payload shape it produces is exercised by the parser tests.
/// </summary>
public sealed class AzureKnowledgeBaseRetrieverTests
{
    private static readonly DateTimeOffset RetrievedAt = new(2026, 6, 26, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ParseGroundingPayload_ValidArray_MapsContentTitleAndRankOrder()
    {
        const string payload = """
        [
          {"ref_id":"r1","title":"Intro","content":"first chunk"},
          {"ref_id":"r2","title":"Body","content":"second chunk"}
        ]
        """;

        var results = AzureKnowledgeBaseRetriever.ParseGroundingPayload(payload, "kb", topK: 10, RetrievedAt);

        results.Should().HaveCount(2);
        results[0].Chunk.Content.Should().Be("first chunk");
        results[0].Chunk.Id.Should().Be("r1");
        results[0].Chunk.SectionPath.Should().Be("Intro");
        results[1].Chunk.Content.Should().Be("second chunk");

        // Server rank order is authoritative; scores decay positionally (1/(1+rank)).
        results[0].FusedScore.Should().Be(1.0);
        results[0].DenseScore.Should().Be(1.0);
        results[1].FusedScore.Should().Be(0.5);
        results.Should().BeInDescendingOrder(r => r.FusedScore);
    }

    [Fact]
    public void ParseGroundingPayload_RespectsTopK()
    {
        const string payload = """
        [{"content":"a"},{"content":"b"},{"content":"c"}]
        """;

        var results = AzureKnowledgeBaseRetriever.ParseGroundingPayload(payload, "kb", topK: 2, RetrievedAt);

        results.Should().HaveCount(2);
        results.Select(r => r.Chunk.Content).Should().Equal("a", "b");
    }

    [Fact]
    public void ParseGroundingPayload_SkipsEntriesWithoutContent_AndKeepsContiguousRank()
    {
        // First entry has no content and must be skipped without consuming a rank slot, so the
        // surviving entry is ranked first (score 1.0) rather than demoted.
        const string payload = """
        [{"ref_id":"empty"},{"ref_id":"keep","content":"kept"}]
        """;

        var results = AzureKnowledgeBaseRetriever.ParseGroundingPayload(payload, "kb", topK: 10, RetrievedAt);

        results.Should().ContainSingle();
        results[0].Chunk.Content.Should().Be("kept");
        results[0].FusedScore.Should().Be(1.0);
    }

    [Fact]
    public void ParseGroundingPayload_MissingRefId_SynthesizesStableId()
    {
        const string payload = """
        [{"content":"no id here"}]
        """;

        var results = AzureKnowledgeBaseRetriever.ParseGroundingPayload(payload, "kb", topK: 10, RetrievedAt);

        results.Should().ContainSingle();
        results[0].Chunk.Id.Should().Be("kb-0");
        results[0].Chunk.SectionPath.Should().BeEmpty();
        results[0].Chunk.Metadata.CreatedAt.Should().Be(RetrievedAt);
    }

    [Theory]
    [InlineData("not valid json {")]
    [InlineData("{\"content\":\"object-not-array\"}")]
    [InlineData("\"a bare string\"")]
    [InlineData("")]
    public void ParseGroundingPayload_MalformedOrNonArray_ReturnsEmpty(string payload)
    {
        var results = AzureKnowledgeBaseRetriever.ParseGroundingPayload(payload, "kb", topK: 10, RetrievedAt);

        results.Should().BeEmpty();
    }

    [Theory]
    [InlineData("agentic-kb")]
    [InlineData("my kb")]    // space — would break a host-segment URI
    [InlineData("über-kb")]  // non-ASCII
    [InlineData("a%b")]      // percent
    [InlineData("")]         // empty
    public void ParseGroundingPayload_ExoticKnowledgeBaseName_DoesNotThrowAndBuildsSourceUri(string knowledgeBaseName)
    {
        // Regression: the synthetic SourceUri must put the knowledge base name in the PATH,
        // not the host — escaping an exotic name into the host segment throws UriFormatException
        // and (since mapping runs outside the client try/catch) would break the fail-soft contract.
        const string payload = """[{"ref_id":"r1","content":"c"}]""";

        var act = () => AzureKnowledgeBaseRetriever.ParseGroundingPayload(payload, knowledgeBaseName, 10, RetrievedAt);

        var results = act.Should().NotThrow().Which;
        results.Should().ContainSingle();
        results[0].Chunk.Metadata.SourceUri.Host.Should().Be("azure-knowledge-base.invalid");
    }

    [Fact]
    public void ParseGroundingPayload_ZeroTopK_ReturnsEmpty()
    {
        const string payload = """[{"content":"a"}]""";

        AzureKnowledgeBaseRetriever.ParseGroundingPayload(payload, "kb", topK: 0, RetrievedAt)
            .Should().BeEmpty();
    }

    [Fact]
    public async Task RetrieveAsync_BackendThrowsRequestFailed_FailsSoftToEmpty()
    {
        var config = new AppConfig();
        config.AI.Rag.AgenticRetrieval.Enabled = true;
        config.AI.Rag.AgenticRetrieval.Endpoint = "https://svc.search.windows.net";
        var monitor = Mock.Of<IOptionsMonitor<AppConfig>>(m => m.CurrentValue == config);

        // KnowledgeBaseRetrievalClient has a protected ctor + virtual RetrieveAsync, so it is
        // mockable; simulate a backend outage and assert the fail-soft contract (no throw).
        var client = new Mock<KnowledgeBaseRetrievalClient>();
        client
            .Setup(c => c.RetrieveAsync(
                It.IsAny<KnowledgeBaseRetrievalRequest>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(503, "service unavailable"));

        var sut = new AzureKnowledgeBaseRetriever(client.Object, monitor, NullLogger<AzureKnowledgeBaseRetriever>.Instance);

        var results = await sut.RetrieveAsync("any query", topK: 5);

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task RetrieveAsync_EnabledButNotConfigured_ReturnsEmptyWithoutCallingService()
    {
        var config = new AppConfig();
        config.AI.Rag.AgenticRetrieval.Enabled = true; // enabled but Endpoint left null -> not configured
        var monitor = Mock.Of<IOptionsMonitor<AppConfig>>(m => m.CurrentValue == config);

        // A real client that must never be called because IsConfigured short-circuits first.
        var client = new KnowledgeBaseRetrievalClient(
            new Uri("https://placeholder.search.windows.net"), "kb", new AzureKeyCredential("unused"));

        var sut = new AzureKnowledgeBaseRetriever(client, monitor, NullLogger<AzureKnowledgeBaseRetriever>.Instance);

        var results = await sut.RetrieveAsync("any query", topK: 5);

        results.Should().BeEmpty();
    }
}
