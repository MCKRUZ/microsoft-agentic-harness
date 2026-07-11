using Application.AI.Common.Interfaces.Bundles;
using Domain.AI.Agents;
using Domain.AI.Bundles;
using Domain.Common.Config;
using FluentAssertions;
using Infrastructure.AI.Bundles;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Infrastructure.AI.Tests.Bundles;

/// <summary>
/// Tests for <see cref="InMemoryBundleHandleStore"/>: sliding TTL, run-pinning that protects an in-flight
/// run's staging directory from the sweeper, and guaranteed directory deletion on removal, expiry, and
/// disposal.
/// </summary>
public sealed class InMemoryBundleHandleStoreTests : IDisposable
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(30);
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 7, 10, 12, 0, 0, TimeSpan.Zero));
    private readonly List<string> _tempDirs = [];

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private InMemoryBundleHandleStore BuildSut()
    {
        var cfg = new AppConfig();
        cfg.AI.BundleExecution.HandleTtl = Ttl;
        return new InMemoryBundleHandleStore(
            new StaticOptionsMonitor<AppConfig>(cfg),
            _time,
            NullLogger<InMemoryBundleHandleStore>.Instance);
    }

    private StagedBundle StageOnDisk(string bundleId)
    {
        var dir = Path.Combine(Path.GetTempPath(), "bundle-handle-tests", bundleId);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "AGENT.md"), "# agent");
        _tempDirs.Add(dir);

        return new StagedBundle
        {
            BundleId = bundleId,
            StagedRootDirectory = dir,
            Agent = new AgentDefinition { Id = "agent-" + bundleId, Name = "Agent " + bundleId }
        };
    }

    [Fact]
    public void Register_ThenTryGet_ReturnsBundle()
    {
        var sut = BuildSut();
        var handle = sut.Register(StageOnDisk("b1"), "owner-1");

        var got = sut.TryGet(handle);

        got.Should().NotBeNull();
        got!.BundleId.Should().Be("b1");
    }

    [Fact]
    public void TryGet_UnknownHandle_ReturnsNull()
    {
        BuildSut().TryGet("nope").Should().BeNull();
    }

    [Fact]
    public void GetOwner_ReturnsRegisteredOwner_AndNullWhenUnknownOrExpired()
    {
        var sut = BuildSut();
        var handle = sut.Register(StageOnDisk("b1"), "owner-42");

        sut.GetOwner(handle).Should().Be("owner-42");
        sut.GetOwner("unknown").Should().BeNull();

        _time.Advance(Ttl + TimeSpan.FromSeconds(1));
        sut.GetOwner(handle).Should().BeNull("an expired handle exposes no owner");
    }

    [Fact]
    public void TryGet_AfterTtlElapses_ReturnsNull()
    {
        var sut = BuildSut();
        var handle = sut.Register(StageOnDisk("b1"), "owner-1");

        _time.Advance(Ttl + TimeSpan.FromSeconds(1));

        sut.TryGet(handle).Should().BeNull();
    }

    [Fact]
    public void TryGet_RefreshesSlidingExpiry()
    {
        var sut = BuildSut();
        var handle = sut.Register(StageOnDisk("b1"), "owner-1");

        // Advance almost to expiry, touch it, then advance another almost-full TTL: still alive.
        _time.Advance(Ttl - TimeSpan.FromMinutes(1));
        sut.TryGet(handle).Should().NotBeNull();
        _time.Advance(Ttl - TimeSpan.FromMinutes(1));

        sut.TryGet(handle).Should().NotBeNull();
    }

    [Fact]
    public void Acquire_PinsHandle_SweepDoesNotDeleteWhileLeased()
    {
        var sut = BuildSut();
        var bundle = StageOnDisk("b1");
        var handle = sut.Register(bundle, "owner-1");

        using (var lease = sut.Acquire(handle))
        {
            lease.Should().NotBeNull();
            lease!.Bundle.BundleId.Should().Be("b1");

            _time.Advance(Ttl + TimeSpan.FromMinutes(5));

            // Pinned: the sweeper must not evict or delete a leased handle even though its clock expiry passed.
            sut.SweepExpired().Should().Be(0);
            Directory.Exists(bundle.StagedRootDirectory).Should().BeTrue();
        }

        // Once released and expired, the sweeper reclaims it and deletes the directory.
        _time.Advance(Ttl + TimeSpan.FromMinutes(5));
        sut.SweepExpired().Should().Be(1);
        Directory.Exists(bundle.StagedRootDirectory).Should().BeFalse();
    }

    [Fact]
    public void Remove_Unleased_DeletesDirectoryImmediately()
    {
        var sut = BuildSut();
        var bundle = StageOnDisk("b1");
        var handle = sut.Register(bundle, "owner-1");

        sut.Remove(handle).Should().BeTrue();

        Directory.Exists(bundle.StagedRootDirectory).Should().BeFalse();
        sut.TryGet(handle).Should().BeNull();
    }

    [Fact]
    public void Remove_WhileLeased_DefersDeletionUntilRelease()
    {
        var sut = BuildSut();
        var bundle = StageOnDisk("b1");
        var handle = sut.Register(bundle, "owner-1");

        var lease = sut.Acquire(handle)!;
        sut.Remove(handle).Should().BeTrue();

        // A removed-but-leased handle is invisible to new lookups but its directory survives the run.
        sut.TryGet(handle).Should().BeNull();
        Directory.Exists(bundle.StagedRootDirectory).Should().BeTrue();

        lease.Dispose();
        Directory.Exists(bundle.StagedRootDirectory).Should().BeFalse();
    }

    [Fact]
    public void Remove_UnknownHandle_ReturnsFalse()
    {
        BuildSut().Remove("nope").Should().BeFalse();
    }

    [Fact]
    public void SweepExpired_DeletesExpiredDirectories_ReturnsCount()
    {
        var sut = BuildSut();
        var b1 = StageOnDisk("b1");
        var b2 = StageOnDisk("b2");
        sut.Register(b1, "owner-1");
        sut.Register(b2, "owner-1");

        _time.Advance(Ttl + TimeSpan.FromSeconds(1));

        sut.SweepExpired().Should().Be(2);
        Directory.Exists(b1.StagedRootDirectory).Should().BeFalse();
        Directory.Exists(b2.StagedRootDirectory).Should().BeFalse();
    }

    [Fact]
    public void Dispose_DeletesAllRemainingDirectories()
    {
        var sut = BuildSut();
        var b1 = StageOnDisk("b1");
        var b2 = StageOnDisk("b2");
        sut.Register(b1, "owner-1");
        sut.Register(b2, "owner-1");

        sut.Dispose();

        Directory.Exists(b1.StagedRootDirectory).Should().BeFalse();
        Directory.Exists(b2.StagedRootDirectory).Should().BeFalse();
    }

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
            catch { /* best-effort test cleanup */ }
        }
    }
}
