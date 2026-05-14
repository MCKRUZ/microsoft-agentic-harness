using Domain.AI.Agents;
using Domain.AI.Governance;
using Domain.AI.Orchestration;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.Orchestration;
using FluentAssertions;
using Infrastructure.AI.Agents;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Agents;

public sealed class JsonlDelegationStoreTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(), $"delegation-store-tests-{Guid.NewGuid():N}");

    private readonly JsonlDelegationStore _store;

    public JsonlDelegationStoreTests()
    {
        var config = new AppConfig
        {
            AI = new AIConfig
            {
                Orchestration = new OrchestrationConfig
                {
                    Subagent = new SubagentConfig { DelegationStoragePath = _tempDir }
                }
            }
        };
        var options = Mock.Of<IOptionsMonitor<AppConfig>>(o => o.CurrentValue == config);
        _store = new JsonlDelegationStore(options, Mock.Of<ILogger<JsonlDelegationStore>>());
    }

    public void Dispose()
    {
        _store.Dispose();
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private static DelegationRecord BuildRecord(
        Guid? delegationId = null,
        string supervisorId = "supervisor-1",
        DelegationState state = DelegationState.Pending,
        Guid? parentDelegationId = null)
        => new()
        {
            DelegationId = delegationId ?? Guid.NewGuid(),
            SupervisorId = supervisorId,
            DelegateAgentId = "agent-1",
            DelegateAgentType = SubagentType.Execute,
            TaskDescription = "test task",
            RequiredCapabilities = ["tool_a"],
            AutonomyLevel = AutonomyLevel.Supervised,
            State = state,
            DelegationDepth = 0,
            StartedAt = DateTimeOffset.UtcNow,
            ParentDelegationId = parentDelegationId
        };

    [Fact]
    public async Task AppendAsync_ThenGetByIdAsync_ReturnsRecord()
    {
        var record = BuildRecord();

        await _store.AppendAsync(record);
        var result = await _store.GetByIdAsync(record.DelegationId);

        result.Should().NotBeNull();
        result!.DelegationId.Should().Be(record.DelegationId);
        result.SupervisorId.Should().Be("supervisor-1");
        result.State.Should().Be(DelegationState.Pending);
    }

    [Fact]
    public async Task AppendAsync_MultipleTimes_GetByIdAsync_ReturnsLatestState()
    {
        var id = Guid.NewGuid();

        await _store.AppendAsync(BuildRecord(delegationId: id, state: DelegationState.Pending));
        await _store.AppendAsync(BuildRecord(delegationId: id, state: DelegationState.Completed));

        var result = await _store.GetByIdAsync(id);

        result.Should().NotBeNull();
        result!.State.Should().Be(DelegationState.Completed);
    }

    [Fact]
    public async Task GetBySessionAsync_ReturnsAllRecordsDeduplicatedById()
    {
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        var id3 = Guid.NewGuid();

        // 6 total lines: each delegation gets 2 state transitions
        await _store.AppendAsync(BuildRecord(delegationId: id1, state: DelegationState.Pending));
        await _store.AppendAsync(BuildRecord(delegationId: id1, state: DelegationState.Completed));
        await _store.AppendAsync(BuildRecord(delegationId: id2, state: DelegationState.Pending));
        await _store.AppendAsync(BuildRecord(delegationId: id2, state: DelegationState.InProgress));
        await _store.AppendAsync(BuildRecord(delegationId: id3, state: DelegationState.Pending));
        await _store.AppendAsync(BuildRecord(delegationId: id3, state: DelegationState.Failed));

        var result = await _store.GetBySessionAsync("supervisor-1");

        result.Should().HaveCount(3);
        result.Select(r => r.DelegationId).Should().BeEquivalentTo([id1, id2, id3]);
    }

    [Fact]
    public async Task GetByParentAsync_ReturnsOnlyChildDelegations()
    {
        var parentId = Guid.NewGuid();
        var child1 = Guid.NewGuid();
        var child2 = Guid.NewGuid();

        // Parent delegation (no parent)
        await _store.AppendAsync(BuildRecord(delegationId: parentId, parentDelegationId: null));

        // Two child delegations
        await _store.AppendAsync(BuildRecord(delegationId: child1, parentDelegationId: parentId));
        await _store.AppendAsync(BuildRecord(delegationId: child2, parentDelegationId: parentId));

        var result = await _store.GetByParentAsync(parentId);

        result.Should().HaveCount(2);
        result.Select(r => r.DelegationId).Should().BeEquivalentTo([child1, child2]);
    }

    [Fact]
    public async Task AppendAsync_CreatesDirectoryStructureLazily()
    {
        Directory.Exists(_tempDir).Should().BeFalse("temp dir should not exist before first append");

        await _store.AppendAsync(BuildRecord());

        Directory.Exists(_tempDir).Should().BeTrue();
    }

    [Fact]
    public async Task AppendAsync_ConcurrentWrites_NoCorruption()
    {
        var ids = Enumerable.Range(0, 10).Select(_ => Guid.NewGuid()).ToList();

        await Task.WhenAll(ids.Select(id => _store.AppendAsync(BuildRecord(delegationId: id))));

        var result = await _store.GetBySessionAsync("supervisor-1");

        result.Should().HaveCount(10);
        result.Select(r => r.DelegationId).Should().BeEquivalentTo(ids);
    }

    [Fact]
    public async Task GetByIdAsync_NonexistentDelegation_ReturnsNull()
    {
        // Append one record so the store has files to search
        await _store.AppendAsync(BuildRecord());

        var result = await _store.GetByIdAsync(Guid.NewGuid());

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetBySessionAsync_NoFile_ReturnsEmpty()
    {
        var result = await _store.GetBySessionAsync("unknown-supervisor");

        result.Should().BeEmpty();
    }
}
