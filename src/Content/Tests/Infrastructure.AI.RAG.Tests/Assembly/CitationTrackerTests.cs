using FluentAssertions;
using Infrastructure.AI.RAG.Assembly;
using Infrastructure.AI.RAG.Tests.Helpers;
using Xunit;

namespace Infrastructure.AI.RAG.Tests.Assembly;

public sealed class CitationTrackerTests
{
    [Fact]
    public void Track_ValidChunk_RecordsCitation()
    {
        var tracker = new CitationTracker();
        var chunk = RagTestData.CreateChunk("c1", "hello world");

        tracker.Track(chunk, assembledOffset: 0, length: 11);

        var citations = tracker.GetCitations();
        citations.Should().HaveCount(1);
        citations[0].ChunkId.Should().Be("c1");
        citations[0].StartOffset.Should().Be(0);
        citations[0].EndOffset.Should().Be(11);
        citations[0].SectionPath.Should().Be(chunk.SectionPath);
        citations[0].DocumentUri.Should().Be(chunk.Metadata.SourceUri);
    }

    [Fact]
    public void Track_MultipleChunks_ReturnsSortedByOffset()
    {
        var tracker = new CitationTracker();
        var chunk1 = RagTestData.CreateChunk("c1", "first");
        var chunk2 = RagTestData.CreateChunk("c2", "second");
        var chunk3 = RagTestData.CreateChunk("c3", "third");

        tracker.Track(chunk3, assembledOffset: 200, length: 5);
        tracker.Track(chunk1, assembledOffset: 0, length: 5);
        tracker.Track(chunk2, assembledOffset: 100, length: 6);

        var citations = tracker.GetCitations();
        citations.Should().HaveCount(3);
        citations[0].ChunkId.Should().Be("c1");
        citations[1].ChunkId.Should().Be("c2");
        citations[2].ChunkId.Should().Be("c3");
    }

    [Fact]
    public void Track_NullChunk_ThrowsArgumentNullException()
    {
        var tracker = new CitationTracker();

        var act = () => tracker.Track(null!, assembledOffset: 0, length: 5);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Track_NegativeOffset_ThrowsArgumentOutOfRange()
    {
        var tracker = new CitationTracker();
        var chunk = RagTestData.CreateChunk();

        var act = () => tracker.Track(chunk, assembledOffset: -1, length: 5);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Track_ZeroLength_ThrowsArgumentOutOfRange()
    {
        var tracker = new CitationTracker();
        var chunk = RagTestData.CreateChunk();

        var act = () => tracker.Track(chunk, assembledOffset: 0, length: 0);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void GetCitations_NoTrackedChunks_ReturnsEmpty()
    {
        var tracker = new CitationTracker();

        tracker.GetCitations().Should().BeEmpty();
    }

    [Fact]
    public void Reset_ClearsTrackedCitations()
    {
        var tracker = new CitationTracker();
        tracker.Track(RagTestData.CreateChunk(), assembledOffset: 0, length: 10);
        tracker.GetCitations().Should().HaveCount(1);

        tracker.Reset();

        tracker.GetCitations().Should().BeEmpty();
    }
}
