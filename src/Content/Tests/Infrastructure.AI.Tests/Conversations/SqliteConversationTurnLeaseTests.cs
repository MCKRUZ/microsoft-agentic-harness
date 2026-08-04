using Application.AI.Common.Interfaces.AI;
using Domain.Common.Config.AI.Conversations;
using FluentAssertions;
using Infrastructure.AI.Conversations;
using Infrastructure.AI.Persistence;
using Infrastructure.AI.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Infrastructure.AI.Tests.Conversations;

/// <summary>
/// <see cref="SqliteConversationTurnLease"/> against the shared
/// <see cref="ConversationTurnLeaseContractTests"/>, plus everything that only exists because this
/// lease is durable: it works between instances that share nothing in memory, it expires, it renews
/// itself, and it says so when it is lost.
/// </summary>
/// <remarks>
/// <para>
/// Runs against a real SQLite <em>file</em>, for the same reason the store's tests do: a
/// <c>:memory:</c> database lives inside one connection, which would serialise every writer by itself
/// and make the cross-instance tests prove nothing.
/// </para>
/// <para>
/// Where expiry matters, each lease instance is given its <strong>own</strong>
/// <see cref="FakeTimeProvider"/>. Advancing one clock fires that instance's renewal timer, so a
/// single shared clock could never be moved past an expiry without the holder renewing first —
/// separate clocks are what make "this lease was not renewed and someone else took it" expressible.
/// </para>
/// </remarks>
public sealed class SqliteConversationTurnLeaseTests : ConversationTurnLeaseContractTests, IDisposable
{
    private const int ExpirySeconds = 60;

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);
    private static readonly DateTimeOffset Origin = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

    private readonly string _tempDir;
    private readonly TestConversationDbContextFactory _contextFactory;
    private readonly SqliteConversationTurnLease _lease;

    /// <summary>Creates an isolated on-disk database and the lease under test.</summary>
    public SqliteConversationTurnLeaseTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"convlease-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _contextFactory = new TestConversationDbContextFactory(Path.Combine(_tempDir, "conversations.db"));

        // The contract tests wait for real handovers, so they need a clock that moves on its own and
        // a poll short enough not to dominate the test run.
        _lease = BuildLease(TimeProvider.System, pollIntervalMs: 10);
    }

    /// <inheritdoc />
    protected override IConversationTurnLease Lease => _lease;

    /// <inheritdoc />
    protected override async Task<string> NewConversationAsync()
    {
        var id = $"conv-{Guid.NewGuid():N}";

        await using var context = _contextFactory.CreateDbContext();
        context.Conversations.Add(new ConversationEntity
        {
            Id = id,
            AgentName = "agent",
            UserId = "owner",
            CreatedAt = Origin,
            UpdatedAt = Origin,
        });
        await context.SaveChangesAsync();

        return id;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // Takes the WAL and shared-memory sidecars with it.
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task AcquireAsync_UnknownConversation_ThrowsInsteadOfWaitingForever()
    {
        // A claim that matches no row is indistinguishable from one blocked by a holder, so without
        // this the caller would poll until its own token gave out, with nothing said about why.
        var act = () => _lease.AcquireAsync($"missing-{Guid.NewGuid():N}");

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*does not exist*");
    }

    [Fact]
    public async Task HeldLease_BlocksASecondInstanceThatSharesNothingButTheDatabase()
    {
        // The reason this implementation exists. Two instances stand in for two host processes: they
        // share no memory, so any serialisation has to come out of the conversation row. The
        // in-process lease fails this by construction.
        var id = await NewConversationAsync();
        var otherHost = BuildLease(TimeProvider.System, pollIntervalMs: 10);

        await using var held = await _lease.AcquireAsync(id);
        var queued = otherHost.AcquireAsync(id);

        await Task.Delay(300);
        queued.IsCompleted.Should().BeFalse("the other host must wait for the turn in flight");

        await held.DisposeAsync();

        await using var taken = await queued.WaitAsync(Patience);
        taken.Should().NotBeNull();
    }

    [Fact]
    public async Task ReleasedLease_ClearsBothColumns()
    {
        // A release that only blanked the owner would leave an expiry pointing at a lease nobody
        // holds; one that only cleared the expiry would leave a row that reads as owned forever.
        var id = await NewConversationAsync();

        await (await _lease.AcquireAsync(id)).DisposeAsync();

        var row = await ReadLeaseAsync(id);
        row.Owner.Should().BeNull();
        row.ExpiresAt.Should().BeNull();
    }

    [Fact]
    public async Task ExpiredLease_IsClaimableByAnotherHost()
    {
        // What stops a host that died mid-turn from blocking its conversation forever. The holder's
        // clock is deliberately left where it was, so its renewal never runs — which is exactly the
        // state a crashed host leaves behind.
        var id = await NewConversationAsync();
        var deadHostClock = new FakeTimeProvider(Origin);
        var survivorClock = new FakeTimeProvider(Origin);
        var deadHost = BuildLease(deadHostClock, pollIntervalMs: 10);
        var survivor = BuildLease(survivorClock, pollIntervalMs: 10);

        _ = await deadHost.AcquireAsync(id);

        // One second short of the expiry: still held, so the survivor must not be able to take it.
        survivorClock.Advance(TimeSpan.FromSeconds(ExpirySeconds - 1));
        var tooEarly = survivor.AcquireAsync(id);
        await Task.Delay(300);
        tooEarly.IsCompleted.Should().BeFalse("the lease has not expired yet");

        survivorClock.Advance(TimeSpan.FromSeconds(2));

        await using var taken = await tooEarly.WaitAsync(Patience);
        taken.Should().NotBeNull();
    }

    [Fact]
    public async Task HeldLease_IsRenewedBeforeItExpires()
    {
        // Turn duration has no upper bound — one slow tool call is enough — so a lease that was only
        // ever stamped once would be taken away from turns that are still running.
        var id = await NewConversationAsync();
        var holderClock = new FakeTimeProvider(Origin);
        var otherClock = new FakeTimeProvider(Origin);
        var holder = BuildLease(holderClock, pollIntervalMs: 10);
        var other = BuildLease(otherClock, pollIntervalMs: 10);

        await using var held = await holder.AcquireAsync(id);
        var stampedAtAcquisition = (await ReadLeaseAsync(id)).ExpiresAt;

        // A third of the expiry is one renewal interval.
        holderClock.Advance(TimeSpan.FromSeconds(ExpirySeconds / 3));
        await WaitForAsync(async () => (await ReadLeaseAsync(id)).ExpiresAt > stampedAtAcquisition);

        // Past the original expiry, but not past the renewed one.
        otherClock.Advance(TimeSpan.FromSeconds(ExpirySeconds + 1));
        var queued = other.AcquireAsync(id);

        await Task.Delay(300);
        queued.IsCompleted.Should().BeFalse("renewal moved the expiry out from under this claim");
    }

    [Fact]
    public async Task StolenLease_CancelsLeaseLostOnTheHolder()
    {
        // Without this a takeover is silent, and the losing host carries on writing to a transcript
        // another host is now writing to — which is the concurrent turn the lease exists to prevent,
        // reintroduced by the mechanism meant to stop it.
        var id = await NewConversationAsync();
        var loserClock = new FakeTimeProvider(Origin);
        var thiefClock = new FakeTimeProvider(Origin);
        var loser = BuildLease(loserClock, pollIntervalMs: 10);
        var thief = BuildLease(thiefClock, pollIntervalMs: 10);

        await using var held = await loser.AcquireAsync(id);
        held.LeaseLost.IsCancellationRequested.Should().BeFalse();

        // The thief's clock is past the expiry the loser stamped, and the loser's clock has not moved,
        // so the loser has not renewed.
        thiefClock.Advance(TimeSpan.FromSeconds(ExpirySeconds + 1));
        await using var stolen = await thief.AcquireAsync(id).WaitAsync(Patience);

        // The loser only finds out when its renewal next runs and matches no row.
        loserClock.Advance(TimeSpan.FromSeconds(ExpirySeconds / 3));

        await WaitForAsync(() => Task.FromResult(held.LeaseLost.IsCancellationRequested));
    }

    [Fact]
    public async Task ReleasedAfterBeingStolen_LeavesTheNewHoldersLeaseIntact()
    {
        // The release is conditional on still owning the lease. Without that condition, the losing
        // host finishing its turn would blank a lease the new holder is actively using, and let a
        // third turn straight in.
        var id = await NewConversationAsync();
        var loserClock = new FakeTimeProvider(Origin);
        var thiefClock = new FakeTimeProvider(Origin);
        var loser = BuildLease(loserClock, pollIntervalMs: 10);
        var thief = BuildLease(thiefClock, pollIntervalMs: 10);

        var held = await loser.AcquireAsync(id);

        thiefClock.Advance(TimeSpan.FromSeconds(ExpirySeconds + 1));
        await using var stolen = await thief.AcquireAsync(id).WaitAsync(Patience);
        var thiefsOwner = (await ReadLeaseAsync(id)).Owner;

        await held.DisposeAsync();

        (await ReadLeaseAsync(id)).Owner.Should().Be(thiefsOwner);
    }

    [Fact]
    public async Task DisposeAsync_WithARenewalInFlight_WaitsForItAndReportsNoLoss()
    {
        // Stopping the timer is only half of disposal. The timer callback *starts* a renewal and
        // returns, so disposing the timer leaves an in-flight renewal running. If disposal released
        // the lease underneath it, that renewal would match no row — and "matched no row" is exactly
        // how a lost lease is detected, so a turn that ended normally would log that another host had
        // taken it and cancel a token nothing was listening to.
        //
        // The gate makes the interleaving deterministic instead of a timing race: it holds the
        // renewal inside its database call while disposal is asked to run.
        var id = await NewConversationAsync();
        var clock = new FakeTimeProvider(Origin);
        var gate = new GatedConversationDbContextFactory(_contextFactory);
        var lease = BuildLease(clock, pollIntervalMs: 10, contextFactory: gate);

        var held = await lease.AcquireAsync(id);
        var lost = held.LeaseLost;

        gate.HoldNextContext();
        clock.Advance(TimeSpan.FromSeconds(ExpirySeconds / 3));
        await gate.WaitUntilHeld();

        var disposal = held.DisposeAsync();

        await Task.Delay(300);
        disposal.IsCompleted.Should().BeFalse("disposal must wait for the renewal it cannot cancel");

        gate.ReleaseHeld();
        await disposal.AsTask().WaitAsync(Patience);

        lost.IsCancellationRequested.Should().BeFalse(
            "the lease ended normally — the drained renewal ran while this handle still owned the row");
        (await ReadLeaseAsync(id)).Owner.Should().BeNull("disposal still released the lease");
    }

    [Theory]
    [InlineData(0, 250)]
    [InlineData(-1, 250)]
    [InlineData(60, 0)]
    [InlineData(60, -5)]
    public void Construction_NonPositiveTiming_ThrowsAtStartupRatherThanAtFirstUse(int expiry, int poll)
    {
        // A zero expiry would make every lease claimable the instant it was taken, and a zero poll
        // would spin on the database. Both are configuration mistakes, and a host that fails to start
        // is a far better report of one than a lease that quietly stops serialising anything.
        var act = () => BuildLease(TimeProvider.System, poll, expiry);

        act.Should().Throw<ArgumentException>();
    }

    // -------------------------------------------------------------------------
    // Fixture
    // -------------------------------------------------------------------------

    private SqliteConversationTurnLease BuildLease(
        TimeProvider timeProvider,
        int pollIntervalMs,
        int expirySeconds = ExpirySeconds,
        IDbContextFactory<ConversationDbContext>? contextFactory = null) =>
        new(
            contextFactory ?? _contextFactory,
            Options.Create(new ConversationsConfig
            {
                TurnLease = new ConversationTurnLeaseConfig
                {
                    ExpirySeconds = expirySeconds,
                    PollIntervalMilliseconds = pollIntervalMs,
                },
            }),
            timeProvider,
            NullLogger<SqliteConversationTurnLease>.Instance,
            new SchemaInitializer<ConversationDbContext>(_contextFactory));

    private async Task<(string? Owner, DateTimeOffset? ExpiresAt)> ReadLeaseAsync(string conversationId)
    {
        await using var context = _contextFactory.CreateDbContext();

        return await context.Conversations
            .AsNoTracking()
            .Where(c => c.Id == conversationId)
            .Select(c => new ValueTuple<string?, DateTimeOffset?>(c.LeaseOwner, c.LeaseExpiresAt))
            .SingleAsync();
    }

    /// <summary>
    /// Polls <paramref name="condition"/> until it holds or <see cref="Patience"/> runs out.
    /// </summary>
    /// <remarks>
    /// Renewal is started from a timer callback and completes on its own, so advancing a fake clock
    /// schedules the work rather than finishing it. Waiting for the effect is what makes these tests
    /// deterministic without reaching inside the handle.
    /// </remarks>
    private static async Task WaitForAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + Patience;

        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
                return;

            await Task.Delay(10);
        }

        Assert.Fail($"Condition was still not met after {Patience}.");
    }

    /// <summary>
    /// Wraps the real factory and can hold exactly one <see cref="CreateDbContextAsync"/> call open
    /// until the test lets it go, so a statement can be pinned mid-flight rather than raced with.
    /// </summary>
    /// <remarks>
    /// The seam is the lease's own injected dependency, so nothing is added to production code to
    /// make this observable — which is the point: a hook that exists only for a test is a hook that
    /// can drift away from what the code really does.
    /// </remarks>
    private sealed class GatedConversationDbContextFactory(
        IDbContextFactory<ConversationDbContext> inner)
        : IDbContextFactory<ConversationDbContext>
    {
        private readonly TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _armed;

        /// <summary>Arms the gate: the next context request is held until <see cref="ReleaseHeld"/>.</summary>
        public void HoldNextContext() => Volatile.Write(ref _armed, 1);

        /// <summary>Completes once a caller is actually held, so the test never races the arming.</summary>
        public Task WaitUntilHeld() => _held.Task;

        /// <summary>Lets the held caller proceed.</summary>
        public void ReleaseHeld() => _release.TrySetResult();

        /// <inheritdoc />
        public ConversationDbContext CreateDbContext() => inner.CreateDbContext();

        /// <inheritdoc />
        public async Task<ConversationDbContext> CreateDbContextAsync(CancellationToken ct = default)
        {
            if (Interlocked.Exchange(ref _armed, 0) == 1)
            {
                _held.TrySetResult();
                await _release.Task;
            }

            return await inner.CreateDbContextAsync(ct);
        }
    }
}
