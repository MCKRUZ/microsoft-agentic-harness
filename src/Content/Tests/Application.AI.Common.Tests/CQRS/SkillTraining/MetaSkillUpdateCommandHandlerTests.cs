using Application.AI.Common.CQRS.SkillTraining.MetaSkillUpdate;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Domain.AI.KnowledgeGraph.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Application.AI.Common.Tests.CQRS.SkillTraining;

public class MetaSkillUpdateCommandHandlerTests
{
    [Fact]
    public async Task Handle_PersistsCombinedMemoryUnderStableKey()
    {
        var memory = new RecordingKnowledgeMemory();
        var sut = new MetaSkillUpdateCommandHandler(memory, NullLogger<MetaSkillUpdateCommandHandler>.Instance);

        var result = await sut.Handle(new MetaSkillUpdateCommand
        {
            RunId = "run-A",
            SkillId = "skill-X",
            Epoch = 2,
            CurrentSkill = "## skill body",
            CurrentScore = 0.75,
            PriorMemory = "## Epoch 1 (score 0.5000)\nSkill length: 10 chars."
        }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        memory.LastKey.Should().Be("skill-training/meta/skill-X/run-A");
        memory.LastEntityType.Should().Be(MetaSkillUpdateCommandHandler.EntityType);
        memory.LastContent.Should().Contain("Epoch 1");
        memory.LastContent.Should().Contain("Epoch 2");
        result.Value.Should().Be(memory.LastContent);
    }

    [Fact]
    public async Task Handle_FirstEpoch_NoPriorMemory_StoresJustNewEntry()
    {
        var memory = new RecordingKnowledgeMemory();
        var sut = new MetaSkillUpdateCommandHandler(memory, NullLogger<MetaSkillUpdateCommandHandler>.Instance);

        await sut.Handle(new MetaSkillUpdateCommand
        {
            RunId = "r", SkillId = "s", Epoch = 1,
            CurrentSkill = "x", CurrentScore = 0.1
        }, CancellationToken.None);

        memory.LastContent.Should().StartWith("## Epoch 1");
        memory.LastContent.Should().NotContain("Epoch 0");
    }

    [Fact]
    public async Task Handle_KnowledgeMemoryThrows_ReturnsScrubbedFailCode()
    {
        var sut = new MetaSkillUpdateCommandHandler(
            new ThrowingKnowledgeMemory("sas-token-SECRET"),
            NullLogger<MetaSkillUpdateCommandHandler>.Instance);

        var result = await sut.Handle(new MetaSkillUpdateCommand
        {
            RunId = "r", SkillId = "s", Epoch = 1,
            CurrentSkill = "x", CurrentScore = 0.1
        }, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Contain(MetaSkillUpdateCommandHandler.PersistFailedCode);
        result.Errors.Should().NotContain(e => e.Contains("SECRET", StringComparison.Ordinal));
    }

    private sealed class RecordingKnowledgeMemory : IKnowledgeMemory
    {
        public string? LastKey { get; private set; }
        public string? LastContent { get; private set; }
        public string? LastEntityType { get; private set; }

        public Task<MemoryWriteDecision> RememberAsync(string key, string content, string entityType = "Fact", CancellationToken cancellationToken = default)
        {
            LastKey = key; LastContent = content; LastEntityType = entityType;
            return Task.FromResult(MemoryWriteDecision.Allow());
        }
        public Task<IReadOnlyList<GraphNode>> RecallAsync(string query, int maxResults = 5, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<GraphNode>>([]);
        public Task ForgetAsync(string key, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ImproveAsync(string userMessage, string assistantResponse, IReadOnlyList<string> relevantNodeIds, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class ThrowingKnowledgeMemory : IKnowledgeMemory
    {
        private readonly string _secret;
        public ThrowingKnowledgeMemory(string secret) => _secret = secret;
        public Task<MemoryWriteDecision> RememberAsync(string key, string content, string entityType = "Fact", CancellationToken cancellationToken = default)
            => throw new InvalidOperationException($"upstream said: {_secret}");
        public Task<IReadOnlyList<GraphNode>> RecallAsync(string query, int maxResults = 5, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<GraphNode>>([]);
        public Task ForgetAsync(string key, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ImproveAsync(string userMessage, string assistantResponse, IReadOnlyList<string> relevantNodeIds, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
