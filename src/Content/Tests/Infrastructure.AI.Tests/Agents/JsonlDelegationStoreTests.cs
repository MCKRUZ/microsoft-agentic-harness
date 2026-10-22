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

    // --- Directory permissions (#640, following #527's precedent) ---

    [Fact]
    public async Task AppendAsync_SupervisorDirectoryIsCreatedOwnerOnly()
    {
        // #640: delegation records hold task descriptions and required capabilities for subagent
        // delegations -- the supervisor subdirectory must never inherit whatever the process
        // umask/ACL happens to grant.
        await _store.AppendAsync(BuildRecord());

        Path.Combine(_tempDir, "supervisor-1").ShouldBeOwnerOnlyDirectory();
    }

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

    [Fact]
    public async Task DeleteNotWrittenSinceAsync_DeletesOnlyIdleSessionFiles_AndEmptyFolders()
    {
        // Retention (#901): a session nothing has written to past the period goes; one still being written stays.
        await _store.AppendAsync(BuildRecord(supervisorId: "idle"));
        await _store.AppendAsync(BuildRecord(supervisorId: "busy"));
        var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromDays(90);
        var idleFile = Directory.GetFiles(Path.Combine(_tempDir, "idle")).Single();
        File.SetLastWriteTimeUtc(idleFile, cutoff.UtcDateTime.AddSeconds(-1));

        var deleted = await _store.DeleteNotWrittenSinceAsync(cutoff, CancellationToken.None);

        deleted.Should().Be(1);
        Directory.GetFiles(Path.Combine(_tempDir, "idle")).Should().BeEmpty();
        (await _store.GetBySessionAsync("busy")).Should().ContainSingle();
    }

    [Fact]
    public async Task DeleteNotWrittenSinceAsync_NeverTouchesFilesItDidNotName()
    {
        // A storage path pointed at a parent of other JSONL logs, such as the audit trails' yyyy-MM-dd day files, must not
        // let the sweep delete them.
        var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromDays(90);
        var old = cutoff.UtcDateTime.AddDays(-1);
        var auditDay = Path.Combine(_tempDir, "governance", "2026-01-01.jsonl");
        var topLevel = Path.Combine(_tempDir, "20260101T000000Z.jsonl");
        var nested = Path.Combine(_tempDir, "a", "b", "20260101T000000Z.jsonl");
        foreach (var file in new[] { auditDay, topLevel, nested })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await File.WriteAllTextAsync(file, "{}\n");
            File.SetLastWriteTimeUtc(file, old);
        }

        var deleted = await _store.DeleteNotWrittenSinceAsync(cutoff, CancellationToken.None);

        deleted.Should().Be(0);
        File.Exists(auditDay).Should().BeTrue();
        File.Exists(topLevel).Should().BeTrue();
        File.Exists(nested).Should().BeTrue();
    }

    [Fact]
    public async Task AppendAsync_StartsANewFileEachDay_SoAFileStopsBeingWrittenAndCanAge()
    {
        // A long-running process appending to one file forever would keep that file's last write recent, so its oldest
        // records would never age out.
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(2026, 10, 21, 23, 0, 0, TimeSpan.Zero));
        var config = new AppConfig();
        config.AI.Orchestration.Subagent.DelegationStoragePath = _tempDir;
        using var store = new JsonlDelegationStore(
            Mock.Of<IOptionsMonitor<AppConfig>>(o => o.CurrentValue == config), Mock.Of<ILogger<JsonlDelegationStore>>(), clock);

        await store.AppendAsync(BuildRecord(supervisorId: "daily"));
        await store.AppendAsync(BuildRecord(supervisorId: "daily"));
        Directory.GetFiles(Path.Combine(_tempDir, "daily")).Should().ContainSingle("records on one day share a file");

        clock.Advance(TimeSpan.FromHours(2));
        await store.AppendAsync(BuildRecord(supervisorId: "daily"));

        Directory.GetFiles(Path.Combine(_tempDir, "daily")).Should().HaveCount(2, "the next day's record starts a new file");
    }

    [Fact]
    public async Task DeleteNotWrittenSinceAsync_ASessionStillOpenInThisProcess_IsRecreatedByItsNextRecord()
    {
        await _store.AppendAsync(BuildRecord(supervisorId: "idle"));
        var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromDays(90);
        File.SetLastWriteTimeUtc(Directory.GetFiles(Path.Combine(_tempDir, "idle")).Single(), cutoff.UtcDateTime.AddDays(-1));
        await _store.DeleteNotWrittenSinceAsync(cutoff, CancellationToken.None);

        await _store.AppendAsync(BuildRecord(supervisorId: "idle"));

        (await _store.GetBySessionAsync("idle")).Should().ContainSingle("only the record written after the sweep remains");
    }

    [Fact]
    public void CurrentRetentionPeriod_FollowsTheSwitch()
    {
        var config = new AppConfig();
        config.AI.Orchestration.Subagent.DelegationStoragePath = _tempDir;
        config.AI.Orchestration.Subagent.DelegationRetention.RetainFor = TimeSpan.FromDays(30);
        using var store = new JsonlDelegationStore(
            Mock.Of<IOptionsMonitor<AppConfig>>(o => o.CurrentValue == config), Mock.Of<ILogger<JsonlDelegationStore>>());

        store.CurrentRetentionPeriod.Should().Be(TimeSpan.FromDays(31), "a day's file is last written up to a day after its first record");
        config.AI.Orchestration.Subagent.DelegationRetention.RetainFor = TimeSpan.FromHours(12);
        store.CurrentRetentionPeriod.Should().BeNull("the sweep refuses a period under a day, so none is enforced");
        config.AI.Orchestration.Subagent.DelegationRetention.RetainFor = TimeSpan.FromDays(30);
        config.AI.Orchestration.Subagent.DelegationRetention.Enabled = false;
        store.CurrentRetentionPeriod.Should().BeNull("a switched-off sweep must not be reported as ageing records out");
    }
}
