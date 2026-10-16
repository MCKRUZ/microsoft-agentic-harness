using System.Text.Json;
using System.Text.Json.Serialization;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Application.AI.Common.Services.SkillTraining;
using Domain.AI.KnowledgeGraph.Models;
using Domain.AI.SkillTraining;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Application.AI.Common.Tests.Services.SkillTraining;

/// <summary>
/// Pins <see cref="ExperienceLibrary"/> (#845): where an entry lives, that nothing carrying grader output
/// is written or served, and that recall returns only the right skill's earlier lessons.
/// </summary>
public sealed class ExperienceLibraryTests
{
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    private static ExperienceEntry Entry(
        string runId = "run-A", int epoch = 1, string domain = "finance", string skillId = "skill-X",
        string lesson = "keep rules short", string cue = "when editing", string evidence = "epoch 1",
        string skill = "# skill", IReadOnlyList<EditTrial>? trials = null) => new()
    {
        Cue = cue,
        Lesson = lesson,
        Evidence = evidence,
        Confidence = 1.0,
        EditVerdicts = trials ?? [new EditTrial(1, "Append \"- a rule\"", EditVerdict.Helped)],
        SkillHash = SkillContentHash.Of(skill),
        SkillContent = skill,
        Domain = domain,
        SkillId = skillId,
        RunId = runId,
        Epoch = epoch
    };

    private static ExperienceLibrary Sut(FakeMemory memory) => new(memory, NullLogger<ExperienceLibrary>.Instance);

    private static GraphNode Node(ExperienceEntry entry, string? name = null) => new()
    {
        Id = "memory:" + Guid.NewGuid(),
        Name = name ?? ExperienceLibrary.BuildKey(entry.Domain, entry.SkillId, entry.RunId, entry.Epoch),
        Type = ExperienceLibrary.EntityType,
        Properties = new Dictionary<string, string> { [GraphNodeMemoryExtensions.ContentPropertyKey] = JsonSerializer.Serialize(entry, Json) }
    };

    [Fact]
    public void BuildKey_NamesDomainSkillRunAndEpoch()
    {
        ExperienceLibrary.BuildKey("finance", "skill-X", "run-A", 3).Should().Be("skill-training/experience/finance/skill-X/run-A/3");
    }

    [Fact]
    public async Task Persist_WritesTheEntryAsJsonUnderItsPosition()
    {
        var memory = new FakeMemory();
        var entry = Entry(epoch: 2);

        var result = await Sut(memory).PersistAsync(entry, CancellationToken.None);

        result.Value.Should().Be(MemoryWriteOutcome.Persisted);
        var write = memory.Writes.Should().ContainSingle().Subject;
        write.Key.Should().Be("skill-training/experience/finance/skill-X/run-A/2");
        write.EntityType.Should().Be("SkillTrainingExperience");
        JsonSerializer.Deserialize<ExperienceEntry>(write.Content, Json).Should().BeEquivalentTo(entry);
    }

    public static TheoryData<string> LeakingFields => new() { "cue", "lesson", "evidence", "skill", "trial" };

    [Theory]
    [MemberData(nameof(LeakingFields))]
    public async Task Persist_AMarkerInAnyTextField_WritesNothing(string field)
    {
        var leak = "{\"OverallVerdict\":\"Fail\"}";
        var entry = field switch
        {
            "cue" => Entry(cue: leak),
            "lesson" => Entry(lesson: leak),
            "evidence" => Entry(evidence: leak),
            "skill" => Entry(skill: leak),
            _ => Entry(trials: [new EditTrial(1, leak, EditVerdict.Helped)])
        };
        var memory = new FakeMemory();

        var result = await Sut(memory).PersistAsync(entry, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Contain(ExperienceLibrary.EvidenceLeakCode);
        memory.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task Persist_MemoryThrows_FailsWithAStableCodeAndNoErrorText()
    {
        var result = await Sut(new FakeMemory { Throw = "sas-SECRET" }).PersistAsync(Entry(), CancellationToken.None);

        result.Errors.Should().Equal(ExperienceLibrary.PersistFailedCode);
    }

    [Fact]
    public async Task Persist_ReportsWhatTheMemoryDidWithIt()
    {
        var quarantined = await Sut(new FakeMemory { Trust = MemoryTrust.Untrusted }).PersistAsync(Entry(), CancellationToken.None);
        var rejected = await Sut(new FakeMemory { Accept = false }).PersistAsync(Entry(), CancellationToken.None);

        quarantined.Value.Should().Be(MemoryWriteOutcome.Quarantined);
        rejected.Value.Should().Be(MemoryWriteOutcome.Rejected);
    }

    [Fact]
    public async Task Recall_ReturnsOnlyThisSkillsEntriesInThisDomain()
    {
        var memory = new FakeMemory
        {
            Recalled =
            [
                Node(Entry(runId: "run-B", lesson: "mine")),
                Node(Entry(runId: "run-B", skillId: "skill-Y", lesson: "other skill")),
                Node(Entry(runId: "run-B", domain: "legal", lesson: "other domain"))
            ]
        };

        var recalled = await Sut(memory).RecallAsync("finance", "skill-X", "run-A", 1, 5, CancellationToken.None);

        recalled.Select(e => e.Lesson).Should().Equal("mine");
    }

    [Fact]
    public async Task Recall_NeverReturnsThisRunsEntriesFromTheAskedEpochOnwards()
    {
        var memory = new FakeMemory
        {
            Recalled =
            [
                Node(Entry(runId: "run-A", epoch: 1, lesson: "this run, epoch 1")),
                Node(Entry(runId: "run-A", epoch: 2, lesson: "this run, epoch 2")),
                Node(Entry(runId: "run-A", epoch: 3, lesson: "this run, epoch 3")),
                Node(Entry(runId: "run-B", epoch: 9, lesson: "another run, epoch 9"))
            ]
        };

        var recalled = await Sut(memory).RecallAsync("finance", "skill-X", "run-A", beforeEpoch: 2, 5, CancellationToken.None);

        recalled.Select(e => e.Lesson).Should().Equal("this run, epoch 1", "another run, epoch 9");
    }

    [Fact]
    public async Task Recall_DropsAnEntryCarryingAGraderMarker()
    {
        var memory = new FakeMemory
        {
            Recalled = [Node(Entry(runId: "run-B", lesson: "{\"OverallVerdict\":\"Pass\"}")), Node(Entry(runId: "run-C", lesson: "clean"))]
        };

        var recalled = await Sut(memory).RecallAsync("finance", "skill-X", "run-A", 1, 5, CancellationToken.None);

        recalled.Select(e => e.Lesson).Should().Equal("clean");
    }

    [Fact]
    public async Task Recall_SkipsUnreadableAndForeignNodes()
    {
        var unreadable = Node(Entry(runId: "run-B")) with
        {
            Properties = new Dictionary<string, string> { [GraphNodeMemoryExtensions.ContentPropertyKey] = "{not json" }
        };
        var foreign = Node(Entry(runId: "run-C", lesson: "wrong key"), name: "memory/some-other-fact");
        var memory = new FakeMemory { Recalled = [unreadable, foreign, Node(Entry(runId: "run-D", lesson: "good"))] };

        var recalled = await Sut(memory).RecallAsync("finance", "skill-X", "run-A", 1, 5, CancellationToken.None);

        recalled.Select(e => e.Lesson).Should().Equal("good");
    }

    [Fact]
    public async Task Recall_IsOrderedByRunThenEpochAndCappedAtTheLimit()
    {
        var memory = new FakeMemory
        {
            Recalled =
            [
                Node(Entry(runId: "run-C", epoch: 1, lesson: "c1")),
                Node(Entry(runId: "run-B", epoch: 2, lesson: "b2")),
                Node(Entry(runId: "run-B", epoch: 1, lesson: "b1"))
            ]
        };

        var recalled = await Sut(memory).RecallAsync("finance", "skill-X", "run-A", 1, 2, CancellationToken.None);

        recalled.Select(e => e.Lesson).Should().Equal("b1", "b2");
    }

    [Fact]
    public async Task Recall_AsksTheMemoryForThisSkillsEntityTypeOnly()
    {
        var memory = new FakeMemory();

        await Sut(memory).RecallAsync("finance", "skill-X", "run-A", 1, 5, CancellationToken.None);

        memory.RecallQueries.Should().ContainSingle().Which.Should().Be(("skill-training/experience/finance/skill-X/", "SkillTrainingExperience"));
    }

    [Fact]
    public async Task Forget_RemovesTheEntryAtOnePosition()
    {
        var memory = new FakeMemory();

        await Sut(memory).ForgetAsync("finance", "skill-X", "run-A", 2, CancellationToken.None);

        memory.Forgotten.Should().Equal("skill-training/experience/finance/skill-X/run-A/2");
    }

    private sealed class FakeMemory : IKnowledgeMemory
    {
        public List<(string Key, string Content, string EntityType)> Writes { get; } = [];
        public List<(string Query, string? EntityType)> RecallQueries { get; } = [];
        public List<string> Forgotten { get; } = [];
        public IReadOnlyList<GraphNode> Recalled { get; init; } = [];
        public string? Throw { get; init; }
        public MemoryTrust Trust { get; init; } = MemoryTrust.Trusted;
        public bool Accept { get; init; } = true;

        public Task<MemoryWriteDecision> RememberAsync(string key, string content, string entityType = "Fact", CancellationToken cancellationToken = default)
        {
            if (Throw is not null)
                throw new InvalidOperationException("upstream said: " + Throw);

            Writes.Add((key, content, entityType));
            return Task.FromResult(new MemoryWriteDecision { Persist = Accept, Trust = Trust });
        }

        public Task<IReadOnlyList<GraphNode>> RecallAsync(string query, int maxResults = 5, string? entityType = null, CancellationToken cancellationToken = default)
        {
            RecallQueries.Add((query, entityType));
            return Task.FromResult(Recalled);
        }

        public Task ForgetAsync(string key, CancellationToken cancellationToken = default)
        {
            Forgotten.Add(key);
            return Task.CompletedTask;
        }

        public Task ImproveAsync(string userMessage, string assistantResponse, IReadOnlyList<string> relevantNodeIds, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
