using Application.AI.Common.Interfaces.SkillTraining;
using Application.AI.Common.Services.SkillTraining;
using Domain.AI.SkillTraining;
using FluentAssertions;
using Infrastructure.AI.Persistence;
using Infrastructure.AI.SkillTraining;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Infrastructure.AI.Tests.SkillTraining;

/// <summary>
/// What every <see cref="ISkillTrainingCheckpointStore"/> must do (#853). Run against the in-memory store
/// and the SQLite one, so the durable store is held to exactly the behaviour resume was built on.
/// </summary>
public abstract class SkillTrainingCheckpointStoreContract : IDisposable
{
    protected abstract ISkillTrainingCheckpointStore NewStore();

    public virtual void Dispose()
    {
    }

    protected static SkillTrainingCheckpoint Checkpoint(string runId, int step, double score = 0.5, GateAction action = GateAction.Accept) => new()
    {
        RunId = runId,
        SkillId = "skill-X",
        Step = step,
        Epoch = 1,
        SkillContent = $"# skill at step {step}",
        SkillHash = SkillContentHash.Of($"# skill at step {step}"),
        Score = score,
        Action = action,
        CreatedAt = new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero)
    };

    [Fact]
    public async Task Save_ThenGet_ReturnsTheCheckpoint()
    {
        var store = NewStore();

        await store.SaveAsync(Checkpoint("run-1", 3, 0.7), CancellationToken.None);

        var read = await store.GetAsync("run-1", 3, CancellationToken.None);
        read.Should().NotBeNull();
        (read!.Step, read.Score, read.SkillContent).Should().Be((3, 0.7, "# skill at step 3"));
    }

    [Fact]
    public async Task Save_AtTheSamePosition_ReplacesTheEarlierCheckpoint()
    {
        var store = NewStore();
        await store.SaveAsync(Checkpoint("run-1", 2, 0.2), CancellationToken.None);

        await store.SaveAsync(Checkpoint("run-1", 2, 0.9), CancellationToken.None);

        (await store.ListAsync("run-1", CancellationToken.None)).Should().ContainSingle().Which.Score.Should().Be(0.9);
    }

    [Fact]
    public async Task Get_NothingStored_IsNull()
    {
        (await NewStore().GetAsync("run-1", 1, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task List_ReturnsTheRunsCheckpointsInStepOrder_AndNoOthers()
    {
        var store = NewStore();
        foreach (var step in new[] { 3, 1, 2 })
            await store.SaveAsync(Checkpoint("run-1", step), CancellationToken.None);
        await store.SaveAsync(Checkpoint("run-2", 1), CancellationToken.None);

        var listed = await store.ListAsync("run-1", CancellationToken.None);

        listed.Select(c => c.Step).Should().Equal(1, 2, 3);
        (await store.ListAsync("nobody", CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task GetBest_IsTheHighestScore_AndTheLatestStepOnATie()
    {
        var store = NewStore();
        await store.SaveAsync(Checkpoint("run-1", 1, 0.4), CancellationToken.None);
        await store.SaveAsync(Checkpoint("run-1", 2, 0.8), CancellationToken.None);
        await store.SaveAsync(Checkpoint("run-1", 3, 0.8), CancellationToken.None);
        await store.SaveAsync(Checkpoint("run-1", 4, 0.1), CancellationToken.None);

        (await store.GetBestAsync("run-1", CancellationToken.None))!.Step.Should().Be(3);
        (await store.GetBestAsync("nobody", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task TryBeginRun_WhileAnotherHoldsIt_IsRefused_AndSucceedsAgainOnceReleased()
    {
        var store = NewStore();

        var first = await store.TryBeginRunAsync("run-1", CancellationToken.None);
        var second = await store.TryBeginRunAsync("run-1", CancellationToken.None);
        var other = await store.TryBeginRunAsync("run-2", CancellationToken.None);

        first.Should().NotBeNull();
        second.Should().BeNull("a run already being worked on cannot be claimed again");
        other.Should().NotBeNull("a claim is per run");

        await first!.DisposeAsync();
        var afterRelease = await store.TryBeginRunAsync("run-1", CancellationToken.None);
        afterRelease.Should().NotBeNull();

        await afterRelease!.DisposeAsync();
        await other!.DisposeAsync();
    }
}

public sealed class InMemorySkillTrainingCheckpointStoreTests : SkillTrainingCheckpointStoreContract
{
    protected override ISkillTrainingCheckpointStore NewStore() => new InMemorySkillTrainingCheckpointStore();
}

/// <summary>
/// The SQLite store (#853): the contract above, plus what only a durable, shared store promises.
/// </summary>
public sealed class EfSkillTrainingCheckpointStoreTests : SkillTrainingCheckpointStoreContract
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"skill-training-store-{Guid.NewGuid():N}");
    private string DbPath => Path.Combine(_dir, "training.db");

    public EfSkillTrainingCheckpointStoreTests() => Directory.CreateDirectory(_dir);

    public override void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    protected override ISkillTrainingCheckpointStore NewStore() => Open();

    /// <summary>A store over the shared database file, as a separate process would have its own.</summary>
    private EfSkillTrainingCheckpointStore Open(TimeProvider? time = null)
    {
        var services = new ServiceCollection();
        services.AddDbContextFactory<SkillTrainingDbContext>(o => o.UseSqlite($"DataSource={DbPath}"));
        var factory = services.BuildServiceProvider().GetRequiredService<IDbContextFactory<SkillTrainingDbContext>>();
        return new EfSkillTrainingCheckpointStore(
            factory, new SchemaInitializer<SkillTrainingDbContext>(factory), time ?? TimeProvider.System,
            NullLogger<EfSkillTrainingCheckpointStore>.Instance);
    }

    [Fact]
    public async Task ACheckpointSurvivesTheProcessThatWroteIt()
    {
        await Open().SaveAsync(Checkpoint("run-1", 4, 0.6), CancellationToken.None);

        var afterRestart = await Open().GetAsync("run-1", 4, CancellationToken.None);

        afterRestart!.SkillContent.Should().Be("# skill at step 4");
    }

    [Fact]
    public async Task EveryFieldOfACheckpointRoundTrips()
    {
        var entry = new ExperienceEntry
        {
            Cue = "c", Lesson = "l", Evidence = "e", Confidence = 0.75,
            EditVerdicts = [new EditTrial(2, "Append \"- x\"", EditVerdict.Hurt)],
            SkillHash = "h", SkillContent = "s", Domain = "d", SkillId = "skill-X", RunId = "run-1", Epoch = 2
        };
        var written = Checkpoint("run-1", 5, 0.9, GateAction.AcceptNewBest) with
        {
            Experience = [entry],
            EditTrials = [new EditTrial(5, "Replace \"a\" with \"b\"", EditVerdict.Helped)],
            RunFingerprint = "fingerprint",
            CandidateScore = 0.91,
            ProposedEditCount = 3,
            AppliedEditCount = 2,
            EpochBoundaryDone = true
        };
        await Open().SaveAsync(written, CancellationToken.None);

        var read = await Open().GetAsync("run-1", 5, CancellationToken.None);

        read.Should().BeEquivalentTo(written);
    }

    [Fact]
    public async Task NothingIsEvicted_ARunKeepsItsWholeHistory()
    {
        var store = Open();
        var steps = InMemorySkillTrainingCheckpointStore.MaxCheckpointsPerRun + 20;
        for (var step = 1; step <= steps; step++)
            await store.SaveAsync(Checkpoint("run-1", step, step), CancellationToken.None);

        (await store.ListAsync("run-1", CancellationToken.None)).Should().HaveCount(steps);
    }

    [Fact]
    public async Task AClaimHoldsAcrossStoresSharingTheDatabase()
    {
        var processA = Open();
        var processB = Open();

        await using var claim = await processA.TryBeginRunAsync("run-1", CancellationToken.None);
        var refused = await processB.TryBeginRunAsync("run-1", CancellationToken.None);

        claim.Should().NotBeNull();
        refused.Should().BeNull("another process holds this run");
    }

    [Fact]
    public async Task OfManyProcessesStartingTheSameRunAtOnce_ExactlyOneWins()
    {
        var stores = Enumerable.Range(0, 8).Select(_ => Open()).ToList();

        var claims = await Task.WhenAll(stores.Select(s => s.TryBeginRunAsync("run-1", CancellationToken.None)));

        claims.Count(c => c is not null).Should().Be(1);
        foreach (var claim in claims.Where(c => c is not null))
            await claim!.DisposeAsync();
    }

    [Fact]
    public async Task AClaimLapsesWhenItsHolderStopsRenewing_SoACrashedProcessDoesNotBlockTheRun()
    {
        var crashedClock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero));
        var crashed = Open(crashedClock);
        _ = await crashed.TryBeginRunAsync("run-1", CancellationToken.None);   // never renewed, never released

        var laterClock = new FakeTimeProvider(crashedClock.GetUtcNow() + EfSkillTrainingCheckpointStore.LeaseDuration - TimeSpan.FromSeconds(1));
        (await Open(laterClock).TryBeginRunAsync("run-1", CancellationToken.None))
            .Should().BeNull("the lease has not run out yet");

        laterClock.Advance(TimeSpan.FromSeconds(2));
        var resumed = await Open(laterClock).TryBeginRunAsync("run-1", CancellationToken.None);

        resumed.Should().NotBeNull("the lease has run out, so the run can be resumed");
        await resumed!.DisposeAsync();
    }

    [Fact]
    public async Task ALiveHolderKeepsItsClaimByRenewingIt()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero));
        await using var held = await Open(clock).TryBeginRunAsync("run-1", CancellationToken.None);

        // Past the original expiry, in steps no longer than the renewal interval.
        var contender = new FakeTimeProvider(clock.GetUtcNow());
        var step = EfSkillTrainingCheckpointStore.LeaseDuration / 3;
        for (var i = 0; i < 4; i++)
        {
            clock.Advance(step);
            contender.Advance(step);
            await WaitForLeaseExpiryAtLeastAsync("run-1", clock.GetUtcNow() + EfSkillTrainingCheckpointStore.LeaseDuration);
        }

        (await Open(contender).TryBeginRunAsync("run-1", CancellationToken.None))
            .Should().BeNull("the holder renewed, so its claim outlived the original lease");
    }

    /// <summary>Waits for the holder's timer to have written a renewal, instead of guessing how long that takes.</summary>
    private async Task WaitForLeaseExpiryAtLeastAsync(string runId, DateTimeOffset expected)
    {
        var services = new ServiceCollection();
        services.AddDbContextFactory<SkillTrainingDbContext>(o => o.UseSqlite($"DataSource={DbPath}"));
        var factory = services.BuildServiceProvider().GetRequiredService<IDbContextFactory<SkillTrainingDbContext>>();

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            await using var ctx = await factory.CreateDbContextAsync();
            var lease = await ctx.RunLeases.AsNoTracking().FirstOrDefaultAsync(l => l.RunId == runId);
            if (lease is not null && lease.ExpiresAtUnixMs >= expected.ToUnixTimeMilliseconds())
                return;
            await Task.Delay(20);
        }

        throw new TimeoutException("The holder never renewed its lease.");
    }

    [Fact]
    public async Task ReleasingAClaimLetsAnotherProcessTakeItAtOnce()
    {
        var processA = Open();
        var claim = await processA.TryBeginRunAsync("run-1", CancellationToken.None);

        await claim!.DisposeAsync();

        var taken = await Open().TryBeginRunAsync("run-1", CancellationToken.None);
        taken.Should().NotBeNull();
        await taken!.DisposeAsync();
    }
}
