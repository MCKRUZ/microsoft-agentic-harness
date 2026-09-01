using Application.AI.Common.Interfaces.Context;
using Domain.Common.Config;
using Infrastructure.AI.Context;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Context;

/// <summary>
/// The schedule around <see cref="IToolResultStore.PruneExpiredAsync"/> — that it runs at all, that
/// the switch works, and that a failed sweep costs one tick rather than the service.
/// </summary>
/// <remarks>
/// The sweep's own correctness (which files it deletes) is covered by
/// <see cref="FileSystemToolResultStoreTests"/>'s <c>PruneExpiredAsync_*</c> tests against the real
/// filesystem. This file is deliberately mocked at <see cref="IToolResultStore"/> instead — there is no
/// database to seed the way <c>ConversationBudgetRetentionServiceTests</c> needs one, so the schedule
/// can be proven with nothing but a call counter. Modelled on that file's own harness: same
/// <see cref="ObservableFakeClock"/> mechanism, same five conventions under test.
/// </remarks>
public sealed class ToolResultRetentionServiceTests
{
    private static readonly DateTimeOffset Origin = new(2026, 8, 6, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// How long to wait for a scheduled continuation before calling it a hang — see
    /// <c>ConversationBudgetRetentionServiceTests.Patience</c>'s identical remark.
    /// </summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private readonly ObservableFakeClock _clock = new(Origin);
    private readonly Mock<IToolResultStore> _resultStore = new();

    // Not readonly: one test replaces the whole instance to model what a configuration reload really
    // does — see ConversationBudgetRetentionServiceTests' identical reasoning.
    private AppConfig _config = new();

    public ToolResultRetentionServiceTests()
    {
        _config.AI.ContextManagement.ToolResultRetention.SweepInterval = TimeSpan.FromHours(1);
        _config.AI.ContextManagement.ToolResultRetention.GracePeriod = TimeSpan.FromHours(24);

        _resultStore
            .Setup(s => s.PruneExpiredAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);
    }

    [Fact]
    public async Task Sweeps_OnTheConfiguredInterval()
    {
        using var service = CreateService();
        await StartAndWaitForFirstTimer(service);

        await AdvanceAndSettle(TimeSpan.FromHours(1));

        _resultStore.Verify(
            s => s.PruneExpiredAsync(TimeSpan.FromHours(24), It.IsAny<CancellationToken>()),
            Times.Once, "the first tick after the configured interval must sweep");
    }

    [Fact]
    public async Task Disabled_SweepsNothing()
    {
        _config.AI.ContextManagement.ToolResultRetention.Enabled = false;

        using var service = CreateService();
        await StartAndWaitForFirstTimer(service);

        await AdvanceAndSettle(TimeSpan.FromHours(4));

        _resultStore.Verify(
            s => s.PruneExpiredAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()),
            Times.Never, "a host that switched the sweep off must never call the sweep at all");
    }

    /// <summary>
    /// A configuration reload during the wait is acted on at the end of that wait, not one whole
    /// interval later — see <c>ConversationBudgetRetentionServiceTests</c>' identical test for the
    /// full rationale (a mutation probe found the bug this guards against).
    /// </summary>
    [Fact]
    public async Task ConfigurationReloadedDuringTheWait_IsActedOnAtTheEndOfThatWait()
    {
        _config.AI.ContextManagement.ToolResultRetention.Enabled = false;

        using var service = CreateService();
        await StartAndWaitForFirstTimer(service);

        var reloaded = new AppConfig();
        reloaded.AI.ContextManagement.ToolResultRetention.SweepInterval = TimeSpan.FromHours(1);
        reloaded.AI.ContextManagement.ToolResultRetention.GracePeriod = TimeSpan.FromHours(24);
        reloaded.AI.ContextManagement.ToolResultRetention.Enabled = true;
        _config = reloaded;

        await AdvanceAndSettle(TimeSpan.FromHours(1));

        _resultStore.Verify(
            s => s.PruneExpiredAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()),
            Times.Once, "the switch must be read after the wait, or a reload takes two intervals to take effect");
    }

    /// <summary>
    /// A sweep interval below the floor must be clamped, not honoured — see
    /// <c>ConversationBudgetRetentionServiceTests</c>' identical test for why.
    /// </summary>
    [Fact]
    public async Task SweepIntervalBelowTheFloor_IsClamped()
    {
        _config.AI.ContextManagement.ToolResultRetention.SweepInterval = TimeSpan.FromMilliseconds(1);

        using var service = CreateService();
        await StartAndWaitForFirstTimer(service);

        // No settle on this one, deliberately: half the floor must NOT fire the timer, so there is no
        // iteration to wait for.
        _clock.Advance(TimeSpan.FromSeconds(30));
        _resultStore.Verify(
            s => s.PruneExpiredAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "a one-millisecond interval must have been clamped to the one-minute floor, so half a "
            + "minute is not yet enough");

        await AdvanceAndSettle(TimeSpan.FromSeconds(31));
        _resultStore.Verify(
            s => s.PruneExpiredAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()),
            Times.Once, "past the floor, the sweep runs");
    }

    /// <summary>
    /// A failed sweep costs one tick, not the service — see
    /// <c>ConversationBudgetRetentionServiceTests</c>' identical test for the full rationale.
    /// </summary>
    [Fact]
    public async Task SweepThatThrows_KeepsTheLoopAlive()
    {
        _resultStore
            .Setup(s => s.PruneExpiredAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("disk unavailable"));

        using var service = CreateService();
        await StartAndWaitForFirstTimer(service);

        await AdvanceAndSettle(TimeSpan.FromHours(1));

        _resultStore
            .Setup(s => s.PruneExpiredAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        await AdvanceAndSettle(TimeSpan.FromHours(1));

        _resultStore.Verify(
            s => s.PruneExpiredAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2),
            "the loop must survive a failed sweep, or one bad tick stops retention for the life of the "
            + "process");
    }

    private async Task StartAndWaitForFirstTimer(ToolResultRetentionService service)
    {
        var armed = _clock.NextTimer;
        await service.StartAsync(CancellationToken.None);

        await armed.WaitAsync(Patience);
    }

    private ToolResultRetentionService CreateService() =>
        new(_resultStore.Object, Options(), _clock, NullLogger<ToolResultRetentionService>.Instance);

    /// <summary>
    /// A monitor that reads the field live, so a test replacing the whole <see cref="AppConfig"/> is
    /// visible to the service — what a real reload looks like.
    /// </summary>
    private IOptionsMonitor<AppConfig> Options()
    {
        var monitor = new Mock<IOptionsMonitor<AppConfig>>();
        monitor.Setup(m => m.CurrentValue).Returns(() => _config);
        return monitor.Object;
    }

    /// <summary>
    /// Advances the fake clock and waits until the service's loop has completed that iteration — see
    /// <c>ConversationBudgetRetentionServiceTests.AdvanceAndSettle</c>'s identical remark for why this
    /// cannot be a plain <c>_clock.Advance</c> call.
    /// </summary>
    private async Task AdvanceAndSettle(TimeSpan by)
    {
        var reArmed = _clock.NextTimer;
        _clock.Advance(by);
        await reArmed.WaitAsync(Patience);
    }

    /// <summary>
    /// A <see cref="FakeTimeProvider"/> that also says when something has started waiting on it — see
    /// <c>ConversationBudgetRetentionServiceTests.ObservableFakeClock</c> for the full rationale. Not
    /// shared between the two files: each is a small, self-contained test double, and this codebase's
    /// own convention is many small files over a shared abstraction for exactly this shape of helper.
    /// </summary>
    private sealed class ObservableFakeClock(DateTimeOffset start) : FakeTimeProvider(start)
    {
        private TaskCompletionSource _next = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task NextTimer => Volatile.Read(ref _next).Task;

        public override ITimer CreateTimer(
            TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);

            Interlocked.Exchange(ref _next, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
                .TrySetResult();

            return timer;
        }
    }
}
