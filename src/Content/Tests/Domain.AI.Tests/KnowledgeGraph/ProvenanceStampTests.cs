using Domain.AI.KnowledgeGraph.Models;
using FluentAssertions;
using Xunit;

namespace Domain.AI.Tests.KnowledgeGraph;

/// <summary>
/// Tests for <see cref="ProvenanceStamp"/> record — construction, optional fields, and immutability.
/// </summary>
public sealed class ProvenanceStampTests
{
    [Fact]
    public void Constructor_WithRequiredProperties_SetsValues()
    {
        var timestamp = DateTimeOffset.UtcNow;
        var stamp = new ProvenanceStamp
        {
            SourcePipeline = "rag_ingestion",
            SourceTask = "entity_extraction",
            Timestamp = timestamp
        };

        stamp.SourcePipeline.Should().Be("rag_ingestion");
        stamp.SourceTask.Should().Be("entity_extraction");
        stamp.Timestamp.Should().Be(timestamp);
    }

    [Fact]
    public void Defaults_OptionalProperties_AreNull()
    {
        var stamp = new ProvenanceStamp
        {
            SourcePipeline = "test",
            SourceTask = "test",
            Timestamp = DateTimeOffset.UtcNow
        };

        stamp.SourceDocumentId.Should().BeNull();
        stamp.ExtractionConfidence.Should().BeNull();
        stamp.LastModifiedBy.Should().BeNull();
    }

    [Fact]
    public void AllProperties_CanBePopulated()
    {
        var timestamp = DateTimeOffset.UtcNow;
        var stamp = new ProvenanceStamp
        {
            SourcePipeline = "rag_ingestion",
            SourceTask = "entity_extraction",
            Timestamp = timestamp,
            SourceDocumentId = "doc-123",
            ExtractionConfidence = 0.92,
            LastModifiedBy = "user-456"
        };

        stamp.SourceDocumentId.Should().Be("doc-123");
        stamp.ExtractionConfidence.Should().Be(0.92);
        stamp.LastModifiedBy.Should().Be("user-456");
    }

    [Fact]
    public void WithExpression_CreatesNewStamp_PreservesOriginal()
    {
        var original = new ProvenanceStamp
        {
            SourcePipeline = "rag_ingestion",
            SourceTask = "entity_extraction",
            Timestamp = DateTimeOffset.UtcNow,
            ExtractionConfidence = 0.8
        };

        var modified = original with { ExtractionConfidence = 0.95 };

        modified.ExtractionConfidence.Should().Be(0.95);
        original.ExtractionConfidence.Should().Be(0.8);
    }

    [Fact]
    public void Equality_SameValues_AreEqual()
    {
        var timestamp = DateTimeOffset.Parse("2026-04-23T12:00:00Z");
        var stamp1 = new ProvenanceStamp
        {
            SourcePipeline = "pipeline",
            SourceTask = "task",
            Timestamp = timestamp
        };
        var stamp2 = new ProvenanceStamp
        {
            SourcePipeline = "pipeline",
            SourceTask = "task",
            Timestamp = timestamp
        };

        stamp1.Should().Be(stamp2);
    }

    [Fact]
    public void ExtractionConfidence_ValidRange_AcceptsValues()
    {
        var stamp = new ProvenanceStamp
        {
            SourcePipeline = "test",
            SourceTask = "test",
            Timestamp = DateTimeOffset.UtcNow,
            ExtractionConfidence = 0.0
        };
        stamp.ExtractionConfidence.Should().Be(0.0);

        stamp = stamp with { ExtractionConfidence = 1.0 };
        stamp.ExtractionConfidence.Should().Be(1.0);
    }
}

/// <summary>
/// Tests for <see cref="FeedbackDetectionResult"/> record.
/// </summary>
public sealed class FeedbackDetectionResultTests
{
    [Fact]
    public void NoFeedback_DefaultState()
    {
        var result = new FeedbackDetectionResult
        {
            FeedbackDetected = false,
            ContainsFollowupQuestion = false
        };

        result.FeedbackDetected.Should().BeFalse();
        result.FeedbackText.Should().BeNull();
        result.FeedbackScore.Should().BeNull();
        result.ResponseToUser.Should().BeNull();
        result.ContainsFollowupQuestion.Should().BeFalse();
    }

    [Fact]
    public void PositiveFeedback_AllFieldsPopulated()
    {
        var result = new FeedbackDetectionResult
        {
            FeedbackDetected = true,
            FeedbackText = "That's exactly right!",
            FeedbackScore = 5,
            ResponseToUser = "Glad I could help!",
            ContainsFollowupQuestion = false
        };

        result.FeedbackDetected.Should().BeTrue();
        result.FeedbackScore.Should().Be(5);
    }

    [Fact]
    public void MixedFeedbackAndQuestion_BothFlagsSet()
    {
        var result = new FeedbackDetectionResult
        {
            FeedbackDetected = true,
            FeedbackText = "That's wrong about the API version.",
            FeedbackScore = 2,
            ContainsFollowupQuestion = true
        };

        result.FeedbackDetected.Should().BeTrue();
        result.ContainsFollowupQuestion.Should().BeTrue();
        result.FeedbackScore.Should().Be(2);
    }
}
