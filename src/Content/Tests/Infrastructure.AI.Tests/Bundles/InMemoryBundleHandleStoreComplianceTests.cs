using Domain.AI.Agents;
using Domain.AI.Bundles;
using Domain.AI.Skills;
using Domain.Common.Compliance;
using Domain.Common.Config;
using FluentAssertions;
using Infrastructure.AI.Bundles;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Infrastructure.AI.Tests.Bundles;

/// <summary>
/// Right-to-erasure and export for <see cref="InMemoryBundleHandleStore"/> (#990).
/// </summary>
/// <remarks>
/// A staged bundle's handle names who staged it. Erasure must remove the person's handles and their staging folders, and
/// nobody else's, but never pull the folder out from under a run still reading it: a leased handle goes when its run
/// releases it, and until then it is counted so the receipt says it was kept.
/// </remarks>
public sealed class InMemoryBundleHandleStoreComplianceTests : IDisposable
{
    private const string Subject = "Alice-Oid";

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero));
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bundle-handle-compliance-" + Guid.NewGuid().ToString("N"));

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private InMemoryBundleHandleStore BuildSut() =>
        new(new StaticOptionsMonitor<AppConfig>(new AppConfig()), _time, NullLogger<InMemoryBundleHandleStore>.Instance);

    private StagedBundle Stage(string bundleId)
    {
        var dir = Path.Combine(_root, bundleId);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "AGENT.md"), "# agent");
        return new StagedBundle
        {
            BundleId = bundleId,
            StagedRootDirectory = dir,
            Agent = new AgentDefinition { Id = $"agent-{bundleId}", Name = $"Agent {bundleId}" },
            OwnedSkills = [new SkillDefinition { Id = $"skill-{bundleId}", Name = $"Skill {bundleId}" }],
        };
    }

    [Fact]
    public async Task EraseSubjectAsync_TheSubjectsHandles_AreRemovedWithTheirFolders_AndOthersKept()
    {
        var sut = BuildSut();
        var mine = Stage("mine");
        var mineRecased = Stage("mine-recased");
        var theirs = Stage("theirs");
        sut.Register(mine, Subject);
        sut.Register(mineRecased, Subject.ToLowerInvariant());
        sut.Register(theirs, "bob-oid");

        var evicted = await sut.EraseSubjectAsync(new ErasureSubject(Subject, []), CancellationToken.None);

        evicted.Should().Be(2, "the owner is matched canonically");
        sut.TryGet("mine").Should().BeNull();
        sut.TryGet("mine-recased").Should().BeNull();
        Directory.Exists(mine.StagedRootDirectory).Should().BeFalse("the staging folder goes with the handle");
        sut.TryGet("theirs").Should().NotBeNull("another person's bundle is never erased");
        Directory.Exists(theirs.StagedRootDirectory).Should().BeTrue();
        (await sut.CountRunsKeptAsync(Subject, CancellationToken.None)).Should().Be(0);
    }

    [Fact]
    public async Task EraseSubjectAsync_AHandleARunHolds_IsKeptUntilTheRunReleasesIt()
    {
        var sut = BuildSut();
        var bundle = Stage("leased");
        sut.Register(bundle, Subject);
        var lease = sut.Acquire("leased");
        lease.Should().NotBeNull();

        var evicted = await sut.EraseSubjectAsync(new ErasureSubject(Subject, []), CancellationToken.None);

        evicted.Should().Be(0);
        Directory.Exists(bundle.StagedRootDirectory).Should().BeTrue("the run is still reading its definitions from the folder");
        sut.TryGet("leased").Should().BeNull("a removed handle cannot be used again, even before it is evicted");
        (await sut.CountRunsKeptAsync(Subject, CancellationToken.None)).Should().Be(1, "the receipt must say it was kept");

        lease!.Dispose();

        Directory.Exists(bundle.StagedRootDirectory).Should().BeFalse("the run released it, so erasure's removal completes");
        (await sut.CountRunsKeptAsync(Subject, CancellationToken.None)).Should().Be(0);
    }

    [Fact]
    public async Task ExportSubjectAsync_NamesWhatTheSubjectStaged_WithoutTheServerFolder()
    {
        var sut = BuildSut();
        var bundle = Stage("mine");
        sut.Register(bundle, Subject);
        sut.Register(Stage("theirs"), "bob-oid");

        var exported = await sut.ExportSubjectAsync(new ExportSubject(Subject, []), CancellationToken.None);

        var record = exported.Should().ContainSingle().Subject;
        record.GetProperty("handle").GetString().Should().Be("mine");
        record.GetProperty("agentId").GetString().Should().Be("agent-mine");
        record.GetProperty("skills")[0].GetString().Should().Be("Skill mine");
        record.GetProperty("inUseByRun").GetBoolean().Should().BeFalse();
        record.GetRawText().Should().NotContain("bundle-handle-compliance-",
            "where the host extracted the bundle is the server's detail, not the person's data");
        record.GetRawText().Should().NotContain("theirs");
    }
}
