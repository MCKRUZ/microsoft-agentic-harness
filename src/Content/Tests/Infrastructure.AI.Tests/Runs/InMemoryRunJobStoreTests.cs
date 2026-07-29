using Application.AI.Common.Interfaces.Runs;
using Domain.AI.Bundles;
using Domain.AI.Runs;
using Domain.Common.Config;
using FluentAssertions;
using Infrastructure.AI.Runs;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Infrastructure.AI.Tests.Runs;

/// <summary>
/// Tests for <see cref="InMemoryRunJobStore"/>.
/// </summary>
/// <remarks>
/// Two properties carry real consequences and are tested hardest: a run must be armed exactly once,
/// because duplicate execution here is duplicate model and tool spend rather than a duplicate row;
/// and a run must be invisible to anyone but its owner, because the job identifier is the only thing
/// standing between callers.
/// </remarks>
public sealed class InMemoryRunJobStoreTests
{
    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero));
    private readonly AppConfig _config = new();

    private IRunJobStore BuildSut(TimeSpan? ttl = null)
    {
        _config.AI.WorkflowSubmission.RunRecordTtl = ttl ?? TimeSpan.FromHours(1);
        return new InMemoryRunJobStore(new StaticOptionsMonitor<AppConfig>(_config), _time);
    }

    private RunRecord Queued(string jobId = "job-1", string ownerId = "alice") => new()
    {
        JobId = jobId,
        Kind = RunKind.Workflow,
        TargetId = Guid.NewGuid().ToString(),
        OwnerId = ownerId,
        Envelope = new CapabilityEnvelope(),
        Status = RunStatus.Queued,
        CreatedAt = _time.GetUtcNow()
    };

    [Fact]
    public void TryBeginRun_UnderConcurrency_ArmsExactlyOnce()
    {
        // The property duplicate execution depends on. A redelivered queue message or a second
        // dispatcher must lose, not run the same workflow again.
        var sut = BuildSut();
        sut.Create(Queued());

        var winners = 0;
        var threads = Enumerable.Range(0, 32).Select(_ => new Thread(() =>
        {
            if (sut.TryBeginRun("job-1", _time.GetUtcNow()) is not null)
                Interlocked.Increment(ref winners);
        })).ToList();

        foreach (var thread in threads) thread.Start();
        foreach (var thread in threads) thread.Join();

        winners.Should().Be(1, "exactly one caller may claim a queued run");
    }

    [Fact]
    public void TryBeginRun_AlreadyRunning_ReturnsNull()
    {
        var sut = BuildSut();
        sut.Create(Queued());

        sut.TryBeginRun("job-1", _time.GetUtcNow()).Should().NotBeNull();
        sut.TryBeginRun("job-1", _time.GetUtcNow()).Should().BeNull();
    }

    [Fact]
    public void TryBeginRun_RecordsTheClaimTimeAndMovesToRunning()
    {
        var sut = BuildSut();
        sut.Create(Queued());
        var claimedAt = _time.GetUtcNow();

        var claimed = sut.TryBeginRun("job-1", claimedAt);

        claimed!.Status.Should().Be(RunStatus.Running);
        claimed.StartedAt.Should().Be(claimedAt);
    }

    [Fact]
    public void Get_ByAnotherOwner_IsIndistinguishableFromMissing()
    {
        var sut = BuildSut();
        sut.Create(Queued(ownerId: "alice"));

        sut.Get("job-1", "mallory").Should().BeNull();
        sut.Get("no-such-job", "mallory").Should().BeNull();
        sut.Get("job-1", "alice").Should().NotBeNull();
    }

    [Fact]
    public void Get_OwnerComparison_IsOrdinal()
    {
        // Ordinal everywhere, matching how ownership is compared elsewhere. A case-insensitive match
        // here would let two identities the rest of the system treats as distinct read each other.
        var sut = BuildSut();
        sut.Create(Queued(ownerId: "Alice"));

        sut.Get("job-1", "alice").Should().BeNull();
    }

    [Fact]
    public void SweepExpired_NeverReclaimsAnUnfinishedRun()
    {
        // A queued or running job the caller is still polling must not vanish, however long it takes.
        var sut = BuildSut(ttl: TimeSpan.FromMinutes(5));
        sut.Create(Queued());

        _time.Advance(TimeSpan.FromDays(7));

        sut.SweepExpired().Should().Be(0);
        sut.Get("job-1", "alice").Should().NotBeNull();
    }

    [Fact]
    public void SweepExpired_ReclaimsAFinishedRunOnlyAfterItsRetentionElapses()
    {
        var sut = BuildSut(ttl: TimeSpan.FromMinutes(5));
        sut.Create(Queued());
        var claimed = sut.TryBeginRun("job-1", _time.GetUtcNow())!;
        sut.Update(claimed with { Status = RunStatus.Succeeded, CompletedAt = _time.GetUtcNow() });

        _time.Advance(TimeSpan.FromMinutes(4));
        sut.SweepExpired().Should().Be(0);

        _time.Advance(TimeSpan.FromMinutes(2));
        sut.SweepExpired().Should().Be(1);
        sut.Get("job-1", "alice").Should().BeNull();
    }

    [Fact]
    public void Update_RestartsRetentionFromCompletionNotAcceptance()
    {
        // A run that waited a long time in the queue still gets its full readable window afterwards.
        var sut = BuildSut(ttl: TimeSpan.FromMinutes(10));
        sut.Create(Queued());

        _time.Advance(TimeSpan.FromMinutes(9));
        var claimed = sut.TryBeginRun("job-1", _time.GetUtcNow())!;
        sut.Update(claimed with { Status = RunStatus.Succeeded, CompletedAt = _time.GetUtcNow() });

        _time.Advance(TimeSpan.FromMinutes(5));

        sut.SweepExpired().Should().Be(0, "retention runs from completion, not from acceptance");
        sut.Get("job-1", "alice").Should().NotBeNull();
    }

    [Fact]
    public void CountActiveRuns_CountsOnlyTheCallersUnfinishedRuns()
    {
        var sut = BuildSut();
        sut.Create(Queued("a", "alice"));
        sut.Create(Queued("b", "alice"));
        sut.Create(Queued("c", "mallory"));

        var finished = sut.TryBeginRun("b", _time.GetUtcNow())!;
        sut.Update(finished with { Status = RunStatus.Succeeded, CompletedAt = _time.GetUtcNow() });

        sut.CountActiveRuns("alice").Should().Be(1, "a finished run is history, not load");
        sut.CountActiveRuns("mallory").Should().Be(1);
        sut.CountActiveRuns("nobody").Should().Be(0);
    }

    [Fact]
    public void Create_WithADuplicateJobId_Throws()
    {
        var sut = BuildSut();
        sut.Create(Queued());

        var act = () => sut.Create(Queued());

        act.Should().Throw<InvalidOperationException>();
    }
}
