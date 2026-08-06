using Domain.Common.Config;
using FluentAssertions;
using Infrastructure.AI.Conversations;
using Infrastructure.AI.Persistence;
using Infrastructure.AI.Persistence.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Conversations;

/// <summary>
/// The schedule around <see cref="SqliteConversationBudgetTracker.SweepAbandonedAsync"/> — that it runs
/// at all, that the switch works, and that a failed sweep costs one tick rather than the service.
/// </summary>
/// <remarks>
/// The sweep's own correctness is covered by <see cref="SqliteConversationBudgetSweepTests"/>. What is
/// left here is everything that has ever made a background sweep a claim rather than a behaviour: a
/// service that never ticks, a switch read once at startup, or a single failure that silently ends the
/// loop for the life of the process.
/// </remarks>
public sealed class ConversationBudgetRetentionServiceTests : IDisposable
{
    private static readonly DateTimeOffset Origin = new(2026, 8, 6, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// How long to wait for a scheduled continuation before calling it a hang. Generous on purpose:
    /// these waits end as soon as the loop signals, so the only run that pays this is one that is
    /// genuinely stuck, and a CI machine under load must not be the difference between green and red.
    /// </summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private readonly string _tempDir;
    private readonly TestConversationDbContextFactory _contextFactory;
    private readonly ObservableFakeClock _clock = new(Origin);

    // Not readonly: one test replaces the whole instance to model what a configuration reload really
    // does. Mutating a shared instance instead is what let an earlier version of that test pass against
    // an implementation reading stale configuration.
    private AppConfig _config = new();
    private readonly SqliteConversationBudgetTracker _tracker;

    /// <summary>Creates an isolated on-disk database and the tracker the service sweeps through.</summary>
    public ConversationBudgetRetentionServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"convretention-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _contextFactory = new TestConversationDbContextFactory(Path.Combine(_tempDir, "conversations.db"));

        _config.AI.AgentFramework.ConversationTokenBudget = 10_000;
        _config.AI.Conversations.BudgetRetention.SweepInterval = TimeSpan.FromHours(1);
        _config.AI.Conversations.BudgetRetention.GracePeriod = TimeSpan.FromDays(30);

        _tracker = new SqliteConversationBudgetTracker(
            _contextFactory,
            Options(),
            _clock,
            NullLogger<SqliteConversationBudgetTracker>.Instance,
            new SchemaInitializer<ConversationDbContext>(_contextFactory));
    }

    [Fact]
    public async Task Sweeps_OnTheConfiguredInterval()
    {
        await GivenAnOrphanedRowOlderThanTheGracePeriod();

        // Control: nothing has swept yet, so a service that deleted on construction — or a test that
        // measured after the fact — could not be mistaken for one that swept on schedule.
        BudgetKeys().Should().ContainSingle("control: the row must survive until a tick happens");

        using var service = CreateService();
        await StartAndWaitForFirstTimer(service);

        await AdvanceAndSettle(TimeSpan.FromHours(1));

        BudgetKeys().Should().BeEmpty("the first tick after the configured interval must sweep");
    }

    [Fact]
    public async Task Disabled_SweepsNothing()
    {
        _config.AI.Conversations.BudgetRetention.Enabled = false;
        await GivenAnOrphanedRowOlderThanTheGracePeriod();

        using var service = CreateService();
        await StartAndWaitForFirstTimer(service);

        await AdvanceAndSettle(TimeSpan.FromHours(4));

        BudgetKeys().Should().ContainSingle("a host that switched the sweep off keeps its rows");
    }

    /// <summary>
    /// A configuration reload during the wait is acted on at the end of that wait, not one whole
    /// interval later.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This models a reload the way <c>IOptionsMonitor</c> really does it — by handing back a
    /// <strong>new</strong> <see cref="AppConfig"/> instance. That detail is the whole test. An earlier
    /// version mutated one shared instance, which meant a service that snapshotted the config before its
    /// wait still saw the change, and the test passed against both the correct implementation and the
    /// one that reads six-hour-old configuration. A mutation probe found it by passing when it should
    /// have failed.
    /// </para>
    /// <para>
    /// One interval versus two matters at the shipped default: a host turning the sweep on would wait
    /// twelve hours rather than six, with nothing to indicate why.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ConfigurationReloadedDuringTheWait_IsActedOnAtTheEndOfThatWait()
    {
        _config.AI.Conversations.BudgetRetention.Enabled = false;
        await GivenAnOrphanedRowOlderThanTheGracePeriod();

        using var service = CreateService();
        await StartAndWaitForFirstTimer(service);

        // Replace the instance rather than mutate it, exactly as a reload does. A service holding the
        // old object now holds a stale one, which is the difference this test exists to detect.
        var reloaded = CloneConfig();
        reloaded.AI.Conversations.BudgetRetention.Enabled = true;
        _config = reloaded;

        await AdvanceAndSettle(TimeSpan.FromHours(1));

        BudgetKeys().Should().BeEmpty(
            "the switch must be read after the wait, or a reload takes two intervals to take effect");
    }

    /// <summary>
    /// A sweep interval below the floor must be clamped, not honoured. A positive but tiny value is
    /// valid configuration and would otherwise turn this into a delete loop against the same database
    /// the turn lease serialises on.
    /// </summary>
    [Fact]
    public async Task SweepIntervalBelowTheFloor_IsClamped()
    {
        _config.AI.Conversations.BudgetRetention.SweepInterval = TimeSpan.FromMilliseconds(1);
        await GivenAnOrphanedRowOlderThanTheGracePeriod();

        using var service = CreateService();
        await StartAndWaitForFirstTimer(service);

        // No settle on this one, deliberately: half the floor must NOT fire the timer, so there is no
        // iteration to wait for. Nothing can have run, which is what makes the assertion immediately
        // safe rather than merely early.
        _clock.Advance(TimeSpan.FromSeconds(30));
        BudgetKeys().Should().ContainSingle(
            "a one-millisecond interval must have been clamped to the one-minute floor, so half a minute "
            + "is not yet enough");

        await AdvanceAndSettle(TimeSpan.FromSeconds(31));
        BudgetKeys().Should().BeEmpty("past the floor, the sweep runs");
    }

    /// <summary>
    /// Starts the service and waits until its first wait is actually registered against the fake clock.
    /// </summary>
    /// <remarks>
    /// <c>StartAsync</c> returns as soon as the background loop yields, which may be before it has asked
    /// the clock for a timer. Advancing a <see cref="FakeTimeProvider"/> only fires timers that already
    /// exist, so without this wait the advance lands in the gap and nothing ever ticks — the whole test
    /// then passes or fails on thread scheduling. Found the hard way: the first version of these tests
    /// failed for exactly this reason and looked like a broken sweep.
    /// </remarks>
    /// <summary>
    /// A failed sweep costs one tick, not the service.
    /// </summary>
    /// <remarks>
    /// This is the failure that turns a bounded table back into an unbounded one without anything
    /// saying so: an exception escaping the loop ends it for the life of the process, and every
    /// subsequent tick that would have reclaimed rows simply never happens. Reachable as a test only
    /// because the sweep lets its exceptions out — while the tracker swallowed them, this could not be
    /// written at all.
    /// </remarks>
    [Fact]
    public async Task SweepThatThrows_KeepsTheLoopAlive()
    {
        await GivenAnOrphanedRowOlderThanTheGracePeriod();

        // A negative grace period makes the tracker throw. Any failure would do; this one needs no
        // broken database, and it exercises the same catch.
        _config.AI.Conversations.BudgetRetention.GracePeriod = TimeSpan.FromDays(-1);

        using var service = CreateService();
        await StartAndWaitForFirstTimer(service);

        await AdvanceAndSettle(TimeSpan.FromHours(1));

        // Control: the failing tick really did fail — nothing was reclaimed.
        BudgetKeys().Should().ContainSingle();

        _config.AI.Conversations.BudgetRetention.GracePeriod = TimeSpan.FromDays(30);
        await AdvanceAndSettle(TimeSpan.FromHours(1));

        BudgetKeys().Should().BeEmpty(
            "the loop must survive a failed sweep, or one bad tick stops retention for the life of the "
            + "process and the table silently resumes growing");
    }

    private async Task StartAndWaitForFirstTimer(ConversationBudgetRetentionService service)
    {
        var armed = _clock.NextTimer;
        await service.StartAsync(CancellationToken.None);

        await armed.WaitAsync(Patience);
    }

    private ConversationBudgetRetentionService CreateService() =>
        new(_tracker, Options(), _clock, NullLogger<ConversationBudgetRetentionService>.Instance);

    /// <summary>
    /// A monitor that reads the field live, so a test replacing the whole <see cref="AppConfig"/> is
    /// visible to the service — which is what a real reload looks like.
    /// </summary>
    private IOptionsMonitor<AppConfig> Options()
    {
        var monitor = new Mock<IOptionsMonitor<AppConfig>>();
        monitor.Setup(m => m.CurrentValue).Returns(() => _config);
        return monitor.Object;
    }

    private AppConfig CloneConfig()
    {
        var clone = new AppConfig();
        clone.AI.AgentFramework.ConversationTokenBudget = _config.AI.AgentFramework.ConversationTokenBudget;

        var from = _config.AI.Conversations.BudgetRetention;
        var to = clone.AI.Conversations.BudgetRetention;
        to.Enabled = from.Enabled;
        to.SweepInterval = from.SweepInterval;
        to.GracePeriod = from.GracePeriod;

        return clone;
    }

    /// <summary>
    /// Advances the fake clock and waits until the service's loop has completed that iteration.
    /// </summary>
    /// <remarks>
    /// Advancing a <see cref="FakeTimeProvider"/> fires the timer synchronously, but what it releases is
    /// an <c>await</c> whose continuation is scheduled — so the sweep runs on another thread shortly
    /// afterwards, and every assertion here would otherwise race it. The signal is the loop arming its
    /// <em>next</em> timer, which cannot happen until the current iteration is done; captured before the
    /// advance so a fast iteration cannot complete before anyone is listening. No polling and no
    /// wall-clock assumptions, so a slow machine makes this slower rather than red.
    /// </remarks>
    private async Task AdvanceAndSettle(TimeSpan by)
    {
        var reArmed = _clock.NextTimer;
        _clock.Advance(by);
        await reArmed.WaitAsync(Patience);
    }

    private async Task GivenAnOrphanedRowOlderThanTheGracePeriod()
    {
        // No conversation row is ever created for this key, which is what makes it an orphan.
        await _tracker.RecordUsageAsync("orphan", 100);
        _clock.Advance(TimeSpan.FromDays(31));
    }

    private List<string> BudgetKeys()
    {
        using var context = _contextFactory.CreateDbContext();
        return [.. context.ConversationBudgets.Select(b => b.BudgetKey)];
    }

    /// <summary>Releases the temporary database directory.</summary>
    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    /// <summary>
    /// A <see cref="FakeTimeProvider"/> that also says when something has started waiting on it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="FakeTimeProvider"/> fires only timers that already exist when the clock is advanced,
    /// and it exposes no way to ask whether any are pending. Without a signal, a test has to guess how
    /// long to wait before advancing — a race dressed as a test.
    /// <c>Task.Delay(TimeSpan, TimeProvider, CancellationToken)</c> goes through <c>CreateTimer</c>, so
    /// intercepting that is an exact answer rather than an estimate.
    /// </para>
    /// <para>
    /// Deliberately multi-shot. A one-shot signal covers only the service's first wait, and every
    /// subsequent advance then races the loop re-arming — which is the same bug one iteration further
    /// along. Because the loop creates a timer at the top of every iteration, awaiting the next
    /// creation is also a precise "the previous iteration has finished its work" signal, which is what
    /// removes wall-clock polling from these tests entirely.
    /// </para>
    /// </remarks>
    private sealed class ObservableFakeClock(DateTimeOffset start) : FakeTimeProvider(start)
    {
        private TaskCompletionSource _next = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// Completes when the next timer is created. Capture this <em>before</em> advancing the clock —
        /// capturing it afterwards can miss a creation that already happened.
        /// </summary>
        public Task NextTimer => Volatile.Read(ref _next).Task;

        /// <inheritdoc />
        public override ITimer CreateTimer(
            TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);

            // Install the replacement first, then complete the one callers are holding, so a
            // continuation that immediately asks for the next signal cannot get the one just fired.
            Interlocked.Exchange(ref _next, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
                .TrySetResult();

            return timer;
        }
    }
}
