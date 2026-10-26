using Domain.AI.Learnings;
using Domain.Common.Compliance;
using FluentAssertions;
using Infrastructure.AI.KnowledgeGraph.Compliance;
using Infrastructure.AI.KnowledgeGraph.InMemory;
using Infrastructure.AI.KnowledgeGraph.Learnings;
using Infrastructure.AI.KnowledgeGraph.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Infrastructure.AI.KnowledgeGraph.Tests.Compliance;

/// <summary>
/// Learnings have no exporter of their own: <c>GraphLearningsStore</c> says it exports by owner because its entries are
/// owner-stamped knowledge-graph nodes, which <see cref="KnowledgeGraphExporter"/> returns with the person's other nodes
/// (#902). These prove that claim through the real store and the real exporter over one graph.
/// </summary>
public sealed class LearningsInKnowledgeGraphExportTests
{
    private readonly InMemoryGraphStore _graph = new(NullLogger<InMemoryGraphStore>.Instance);

    [Fact]
    public async Task ALearningSavedByThePerson_IsInTheirKnowledgeGraphSection_AndNotInAnyoneElses()
    {
        await LearningsFor("  User-42  ").SaveAsync(Entry("prefers metric units"), CancellationToken.None);
        await LearningsFor("someone-else").SaveAsync(Entry("not the person's"), CancellationToken.None);

        var mine = Text(await new KnowledgeGraphExporter(_graph).ExportSubjectAsync(new ExportSubject("USER-42", []), CancellationToken.None));
        var theirs = Text(await new KnowledgeGraphExporter(_graph).ExportSubjectAsync(new ExportSubject("someone-else", []), CancellationToken.None));

        mine.Should().Contain("prefers metric units", "the learning store stamps the canonical owner the exporter matches");
        mine.Should().NotContain("not the person's");
        theirs.Should().NotContain("prefers metric units");
    }

    [Fact]
    public async Task ALearningCapturedWithoutACaller_IsInNoOnesCopy()
    {
        await LearningsFor(null).SaveAsync(Entry("captured by background work"), CancellationToken.None);

        var copy = Text(await new KnowledgeGraphExporter(_graph).ExportSubjectAsync(new ExportSubject("user-42", []), CancellationToken.None));

        copy.Should().NotContain("captured by background work", "an unowned learning is shared, never one person's");
    }

    private GraphLearningsStore LearningsFor(string? owner) =>
        new(_graph, StubAmbientRequestScope.ForOwner(owner), NullLogger<GraphLearningsStore>.Instance);

    private static string Text(IEnumerable<System.Text.Json.JsonElement> records) =>
        string.Join("\n", records.Select(r => r.GetRawText()));

    private static LearningEntry Entry(string content) => new()
    {
        LearningId = Guid.NewGuid(),
        Content = content,
        Category = LearningCategory.DomainKnowledge,
        DecayClass = DecayClass.Stable,
        CreatedAt = DateTimeOffset.UtcNow,
        Scope = new LearningScope { IsGlobal = true },
        Source = new LearningSource
        {
            SourceType = LearningSourceType.HumanCorrection,
            SourceId = "test-source",
            SourceDescription = "Test source",
        },
        Provenance = new LearningProvenance
        {
            OriginPipeline = "test-pipeline",
            OriginTask = "test-task",
            OriginTimestamp = DateTimeOffset.UtcNow,
            Confidence = 0.95,
        },
    };
}
