using System.Text.Json;
using Application.AI.Common.CQRS.SkillTraining.MetaSkillUpdate;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Application.AI.Common.Interfaces.SkillTraining;
using Application.AI.Common.Services.SkillTraining;
using Domain.AI.KnowledgeGraph.Models;
using Domain.AI.SkillTraining;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Application.AI.Common.Tests.CQRS.SkillTraining;

/// <summary>
/// Pins the meta-skill update (#845): the epoch's lesson comes from the distiller, is stored only if it
/// carries no grader output, and only earlier positions come back to the loop.
/// </summary>
public class MetaSkillUpdateCommandHandlerTests
{
    private static ExperienceEntry Entry(string lesson = "fine", string runId = "run-A", int epoch = 2, string skillContent = "## skill body") => new()
    {
        Cue = "when editing",
        Lesson = lesson,
        Confidence = 1.0,
        SkillHash = SkillContentHash.Of(skillContent),
        SkillContent = skillContent,
        Domain = "finance",
        SkillId = "skill-X",
        RunId = runId,
        Epoch = epoch
    };

    private static MetaSkillUpdateCommand Command(int epoch = 2, IReadOnlyList<ExperienceEntry>? prior = null) => new()
    {
        RunId = "run-A",
        SkillId = "skill-X",
        Domain = "finance",
        Epoch = epoch,
        CurrentSkill = "## skill body",
        Trials = [new EditTrial(1, "Append \"- a rule\"", EditVerdict.Helped)],
        PriorEntries = prior ?? []
    };

    private static MetaSkillUpdateCommandHandler Sut(IExperienceDistiller distiller, RecordingKnowledgeMemory memory) =>
        new(distiller, new ExperienceLibrary(memory, NullLogger<ExperienceLibrary>.Instance),
            NullLogger<MetaSkillUpdateCommandHandler>.Instance);

    [Fact]
    public async Task Handle_StoresTheLessonUnderItsPositionAndReturnsItAfterThePriorOnes()
    {
        var memory = new RecordingKnowledgeMemory();
        var earlier = Entry("earlier lesson", epoch: 1);
        var sut = Sut(new FixedDistiller(DistillationOutcome.Written(Entry("new lesson"))), memory);

        var result = await sut.Handle(Command(prior: [earlier]), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Stored.Should().BeTrue();
        result.Value.Entries.Select(e => e.Lesson).Should().Equal("earlier lesson", "new lesson");
        memory.Writes.Should().ContainSingle();
        memory.Writes[0].Key.Should().Be("skill-training/experience/finance/skill-X/run-A/2");
        memory.Writes[0].EntityType.Should().Be(ExperienceLibrary.EntityType);
        JsonSerializer.Deserialize<JsonElement>(memory.Writes[0].Content).GetProperty("Lesson").GetString().Should().Be("new lesson");
    }

    [Fact]
    public async Task Handle_NothingToLearn_ReturnsThePriorEntriesAndStoresNothing()
    {
        var memory = new RecordingKnowledgeMemory();
        var sut = Sut(new FixedDistiller(DistillationOutcome.None("nothing reached the gate")), memory);

        var result = await sut.Handle(Command(prior: [Entry("earlier", epoch: 1)]), CancellationToken.None);

        result.Value!.Entries.Should().ContainSingle().Which.Lesson.Should().Be("earlier");
        result.Value.Stored.Should().BeFalse();
        memory.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_EntryContainsGraderMarker_RefusesWrite()
    {
        var memory = new RecordingKnowledgeMemory();
        var leaking = Entry(lesson: "{\"OverallVerdict\":\"Fail\",\"PassedCount\":1}");
        var sut = Sut(new FixedDistiller(DistillationOutcome.Written(leaking)), memory);

        var result = await sut.Handle(Command(prior: [Entry("earlier", epoch: 1)]), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Contain(ExperienceLibrary.EvidenceLeakCode);
        memory.Writes.Should().BeEmpty("a lesson carrying grader output must reach neither the memory nor the run");
    }

    [Fact]
    public async Task Handle_LaterEpochEntry_NeverReachesEarlierStep()
    {
        // A run resumed at epoch 2 can be handed entries an abandoned attempt wrote for epoch 3, and
        // entries from other runs at any epoch. The first must not come back; the second may.
        var memory = new RecordingKnowledgeMemory();
        var prior = new[]
        {
            Entry("this run, epoch 1", runId: "run-A", epoch: 1),
            Entry("this run, epoch 3 (abandoned attempt)", runId: "run-A", epoch: 3),
            Entry("another run, epoch 5", runId: "run-B", epoch: 5)
        };
        var sut = Sut(new FixedDistiller(DistillationOutcome.Written(Entry("new lesson"))), memory);

        var result = await sut.Handle(Command(epoch: 2, prior: prior), CancellationToken.None);

        result.Value!.Entries.Select(e => e.Lesson).Should().Equal("this run, epoch 1", "another run, epoch 5", "new lesson");
    }

    [Fact]
    public async Task Handle_KnowledgeMemoryThrows_KeepsTheLessonInTheRunAndLeaksNothingOfTheError()
    {
        var sut = Sut(new FixedDistiller(DistillationOutcome.Written(Entry("new lesson"))), new RecordingKnowledgeMemory { Throw = "sas-token-SECRET" });

        var result = await sut.Handle(Command(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue("the checkpoint is the run's record; the memory is not");
        result.Value!.Stored.Should().BeFalse();
        result.Value.Entries.Should().ContainSingle().Which.Lesson.Should().Be("new lesson");
        JsonSerializer.Serialize(result).Should().NotContain("SECRET");
    }

    [Fact]
    public async Task Handle_MemoryQuarantinesTheWrite_KeepsTheLessonButReportsItNotStored()
    {
        var memory = new RecordingKnowledgeMemory { Trust = MemoryTrust.Untrusted };
        var sut = Sut(new FixedDistiller(DistillationOutcome.Written(Entry("new lesson"))), memory);

        var result = await sut.Handle(Command(), CancellationToken.None);

        result.Value!.Stored.Should().BeFalse();
        result.Value.Entries.Should().ContainSingle();
    }

    [Fact]
    public async Task Handle_TheDefaultDistiller_WritesAnEntryThatCarriesNoScore()
    {
        var memory = new RecordingKnowledgeMemory();
        var sut = Sut(new EditHistoryDistiller(), memory);

        await sut.Handle(Command(), CancellationToken.None);

        var stored = memory.Writes.Should().ContainSingle().Subject.Content;
        stored.ToLowerInvariant().Should().NotContain("score");
        JsonSerializer.Deserialize<JsonElement>(stored).GetProperty("EditVerdicts")[0].GetProperty("Verdict").GetString().Should().Be("Helped");
    }

    private sealed class FixedDistiller(DistillationOutcome outcome) : IExperienceDistiller
    {
        public Task<DistillationOutcome> DistillAsync(DistillationInput input, CancellationToken cancellationToken) => Task.FromResult(outcome);
    }

    private sealed class RecordingKnowledgeMemory : IKnowledgeMemory
    {
        public List<(string Key, string Content, string EntityType)> Writes { get; } = [];
        public string? Throw { get; init; }
        public MemoryTrust Trust { get; init; } = MemoryTrust.Trusted;

        public Task<MemoryWriteDecision> RememberAsync(string key, string content, string entityType = "Fact", CancellationToken cancellationToken = default)
        {
            if (Throw is not null)
                throw new InvalidOperationException($"upstream said: {Throw}");

            Writes.Add((key, content, entityType));
            return Task.FromResult(new MemoryWriteDecision { Persist = true, Trust = Trust });
        }

        public Task<IReadOnlyList<GraphNode>> RecallAsync(string query, int maxResults = 5, string? entityType = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<GraphNode>>([]);
        public Task ForgetAsync(string key, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ImproveAsync(string userMessage, string assistantResponse, IReadOnlyList<string> relevantNodeIds, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
