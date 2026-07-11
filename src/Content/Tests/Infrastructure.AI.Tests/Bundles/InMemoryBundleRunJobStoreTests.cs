using Domain.AI.Bundles;
using Domain.AI.Governance;
using Domain.Common.Config;
using FluentAssertions;
using Infrastructure.AI.Bundles;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Infrastructure.AI.Tests.Bundles;

/// <summary>
/// Tests for <see cref="InMemoryBundleRunJobStore"/>: create/get/update snapshots, per-record TTL, and the
/// terminal-state TTL extension that keeps a completed run pollable for the full window.
/// </summary>
public sealed class InMemoryBundleRunJobStoreTests
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(30);
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 7, 10, 12, 0, 0, TimeSpan.Zero));

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private InMemoryBundleRunJobStore BuildSut()
    {
        var cfg = new AppConfig();
        cfg.AI.BundleExecution.RunRecordTtl = Ttl;
        return new InMemoryBundleRunJobStore(new StaticOptionsMonitor<AppConfig>(cfg), _time);
    }

    private BundleRunRecord NewRecord(string jobId = "j1", BundleRunStatus status = BundleRunStatus.Queued) => new()
    {
        JobId = jobId,
        Handle = "h1",
        OwnerId = "owner-1",
        AgentName = "agent-1",
        UserMessages = ["hello"],
        MaxTurns = 5,
        Envelope = new CapabilityEnvelope(),
        Status = status,
        CreatedAt = _time.GetUtcNow()
    };

    [Fact]
    public void Create_ThenGet_ReturnsRecord()
    {
        var sut = BuildSut();
        sut.Create(NewRecord());

        sut.Get("j1").Should().NotBeNull();
        sut.Get("j1")!.Status.Should().Be(BundleRunStatus.Queued);
    }

    [Fact]
    public void Create_DuplicateJobId_Throws()
    {
        var sut = BuildSut();
        sut.Create(NewRecord());

        var act = () => sut.Create(NewRecord());

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Get_UnknownJobId_ReturnsNull()
    {
        BuildSut().Get("nope").Should().BeNull();
    }

    [Fact]
    public void Get_NonTerminalRecord_NeverExpires()
    {
        var sut = BuildSut();
        sut.Create(NewRecord()); // Queued

        _time.Advance(Ttl + TimeSpan.FromHours(1));

        // An in-flight (Queued/Running) run is never expired — its record must survive for the dispatcher.
        sut.Get("j1").Should().NotBeNull();
    }

    [Fact]
    public void Get_TerminalRecord_AfterTtlElapses_ReturnsNull()
    {
        var sut = BuildSut();
        sut.Create(NewRecord());
        sut.Update(sut.Get("j1")! with { Status = BundleRunStatus.Succeeded });

        _time.Advance(Ttl + TimeSpan.FromSeconds(1));

        sut.Get("j1").Should().BeNull();
    }

    [Fact]
    public void Update_ReplacesSnapshot()
    {
        var sut = BuildSut();
        sut.Create(NewRecord());

        var updated = sut.Get("j1")! with { Status = BundleRunStatus.Running };
        sut.Update(updated).Should().BeTrue();

        sut.Get("j1")!.Status.Should().Be(BundleRunStatus.Running);
    }

    [Fact]
    public void Update_TerminalStatus_ExtendsTtl()
    {
        var sut = BuildSut();
        sut.Create(NewRecord());

        // Advance almost to the original expiry, then complete the run — which resets the TTL window.
        _time.Advance(Ttl - TimeSpan.FromMinutes(1));
        var done = sut.Get("j1")! with { Status = BundleRunStatus.Succeeded };
        sut.Update(done).Should().BeTrue();

        // Past the ORIGINAL expiry but within the extended window: still pollable.
        _time.Advance(TimeSpan.FromMinutes(2));
        sut.Get("j1").Should().NotBeNull();
        sut.Get("j1")!.Status.Should().Be(BundleRunStatus.Succeeded);
    }

    [Fact]
    public void Update_UnknownJobId_ReturnsFalse()
    {
        BuildSut().Update(NewRecord("ghost")).Should().BeFalse();
    }

    [Fact]
    public void SweepExpired_RemovesExpiredTerminalRecords_ReturnsCount()
    {
        var sut = BuildSut();
        sut.Create(NewRecord("j1"));
        sut.Create(NewRecord("j2"));
        sut.Update(sut.Get("j1")! with { Status = BundleRunStatus.Succeeded });
        sut.Update(sut.Get("j2")! with { Status = BundleRunStatus.Failed });

        _time.Advance(Ttl + TimeSpan.FromSeconds(1));

        sut.SweepExpired().Should().Be(2);
        sut.Get("j1").Should().BeNull();
        sut.Get("j2").Should().BeNull();
    }

    [Fact]
    public void SweepExpired_LeavesNonTerminalRecords()
    {
        var sut = BuildSut();
        sut.Create(NewRecord("j1")); // Queued, in-flight

        _time.Advance(Ttl + TimeSpan.FromHours(1));

        sut.SweepExpired().Should().Be(0);
        sut.Get("j1").Should().NotBeNull();
    }
}
