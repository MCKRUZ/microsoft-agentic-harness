using System.Text.Json;
using Domain.AI.Bundles;
using Domain.AI.Governance;
using Domain.Common.Compliance;
using Domain.Common.Config;
using FluentAssertions;
using Infrastructure.AI.Bundles;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Infrastructure.AI.Tests.Bundles;

/// <summary>
/// Right-to-erasure and export for <see cref="InMemoryBundleRunJobStore"/> (#990).
/// </summary>
/// <remarks>
/// A bundle run record names its caller and keeps the messages they sent and the agent's answer. Erasure must remove the
/// person's records and nobody else's, must never remove a run still in flight (its dispatcher and its caller's poll
/// depend on the record), and the count it leaves must say what was kept, so the receipt cannot read <c>Full</c> over it.
/// </remarks>
public sealed class InMemoryBundleRunJobStoreComplianceTests
{
    private const string Subject = "Alice-Oid";
    private static readonly TimeSpan StreamWindow = TimeSpan.FromMinutes(5);

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero));

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private InMemoryBundleRunJobStore BuildSut()
    {
        var config = new AppConfig();
        config.AI.BundleExecution.StreamReservationTtl = StreamWindow;
        return new InMemoryBundleRunJobStore(new StaticOptionsMonitor<AppConfig>(config), _time);
    }

    private BundleRunRecord Run(string jobId, string ownerId, bool streaming = false) => new()
    {
        JobId = jobId,
        Handle = "handle",
        OwnerId = ownerId,
        AgentName = "agent",
        UserMessages = [$"{jobId} message"],
        MaxTurns = 3,
        ConversationId = $"{jobId}-conversation",
        Envelope = new CapabilityEnvelope(),
        Status = BundleRunStatus.Queued,
        Streaming = streaming,
        CreatedAt = _time.GetUtcNow(),
    };

    private static void Admit(InMemoryBundleRunJobStore store, BundleRunRecord record) =>
        store.TryCreate(record, int.MaxValue).Should().Be(BundleRunAdmission.Accepted);

    private static void Finish(InMemoryBundleRunJobStore store, BundleRunRecord record) =>
        store.Update(record with { Status = BundleRunStatus.Succeeded, CompletedAt = DateTimeOffset.UnixEpoch }).Should().BeTrue();

    private static ErasureSubject SubjectNamed(string ownerId) => new(ownerId, []);

    [Fact]
    public async Task EraseSubjectAsync_FinishedRunsOfTheSubject_AreRemovedAndOthersKept()
    {
        var sut = BuildSut();
        var mine = Run("mine", Subject);
        var mineRecased = Run("mine-recased", Subject.ToLowerInvariant());
        var theirs = Run("theirs", "bob-oid");
        foreach (var run in new[] { mine, mineRecased, theirs })
        {
            Admit(sut, run);
            Finish(sut, run);
        }

        var removed = await sut.EraseSubjectAsync(SubjectNamed(Subject), CancellationToken.None);

        removed.Should().Be(2, "the owner is matched canonically, as the workflow run store matches it");
        sut.Get("mine").Should().BeNull();
        sut.Get("mine-recased").Should().BeNull();
        sut.Get("theirs").Should().NotBeNull("another person's run is never erased");
    }

    [Theory]
    [InlineData(BundleRunStatus.Queued)]
    [InlineData(BundleRunStatus.Running)]
    public async Task EraseSubjectAsync_ARunInFlight_IsKeptAndCounted(BundleRunStatus status)
    {
        var sut = BuildSut();
        var live = Run("live", Subject);
        Admit(sut, live);
        if (status == BundleRunStatus.Running)
            sut.TryBeginRun("live", _time.GetUtcNow()).Should().NotBeNull();

        var removed = await sut.EraseSubjectAsync(SubjectNamed(Subject), CancellationToken.None);

        removed.Should().Be(0);
        sut.Get("live").Should().NotBeNull("removing a run in flight drops it or loses its outcome mid-run");
        (await sut.CountRunsKeptAsync(Subject, CancellationToken.None)).Should().Be(1, "the receipt must say it was kept");
    }

    [Fact]
    public async Task EraseSubjectAsync_AStreamingReservationNobodyConnectedTo_IsRemovedOnceItsWindowPassed()
    {
        var sut = BuildSut();
        Admit(sut, Run("open", Subject, streaming: true));
        Admit(sut, Run("abandoned", Subject, streaming: true));
        _time.Advance(StreamWindow + TimeSpan.FromSeconds(1));
        Admit(sut, Run("fresh", Subject, streaming: true));

        await sut.EraseSubjectAsync(SubjectNamed(Subject), CancellationToken.None);

        // The two older reservations lapsed: no caller can claim them any more, so they are not live.
        (await sut.CountRunsKeptAsync(Subject, CancellationToken.None)).Should().Be(1,
            "only the reservation still inside its connect window is a run a caller can still start");
        sut.TryBeginRun("fresh", _time.GetUtcNow()).Should().NotBeNull();
    }

    [Fact]
    public async Task CountRunsKeptAsync_CountsEveryRecordOfTheSubject_LiveOrNot()
    {
        // A run that ends between the store's erasure and the count is not live but still held.
        var sut = BuildSut();
        var finished = Run("finished", Subject);
        Admit(sut, finished);
        Finish(sut, finished);
        Admit(sut, Run("queued", Subject.ToUpperInvariant()));
        Admit(sut, Run("theirs", "bob-oid"));

        (await sut.CountRunsKeptAsync(Subject, CancellationToken.None)).Should().Be(2);
    }

    [Fact]
    public async Task ExportSubjectAsync_ReturnsEveryRunOfTheSubjectWithTheirMessages_AndNothingOfAnyoneElse()
    {
        var sut = BuildSut();
        var finished = Run("finished", Subject);
        Admit(sut, finished);
        sut.Update(finished with
        {
            Status = BundleRunStatus.Succeeded,
            CompletedAt = _time.GetUtcNow(),
            Outcome = new BundleRunOutcome
            {
                ConversationSucceeded = true, FinalResponse = "finished answer", TurnCount = 1, TotalToolInvocations = 0,
            },
        }).Should().BeTrue();
        Admit(sut, Run("queued", Subject));
        Admit(sut, Run("theirs", "bob-oid"));

        var exported = await sut.ExportSubjectAsync(new ExportSubject(Subject, []), CancellationToken.None);

        var text = string.Join("\n", exported.Select(e => e.GetRawText()));
        exported.Should().HaveCount(2, "a run in flight is still the person's, though erasure keeps it");
        text.Should().Contain("finished message").And.Contain("finished answer").And.Contain("queued message");
        text.Should().NotContain("theirs");
        exported.Select(e => e.GetProperty("status").GetString()).Should().BeEquivalentTo(["Succeeded", "Queued"],
            "enums are written by name, as the workflow run store writes them");
        exported[0].ValueKind.Should().Be(JsonValueKind.Object);
    }
}
