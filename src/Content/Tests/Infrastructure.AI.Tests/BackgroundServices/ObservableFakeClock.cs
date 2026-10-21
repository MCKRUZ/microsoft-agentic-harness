using Microsoft.Extensions.Time.Testing;

namespace Infrastructure.AI.Tests.BackgroundServices;

/// <summary>
/// A <see cref="FakeTimeProvider"/> that also says when something has started waiting on it, for tests of a
/// <c>PeriodicSweepService</c>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="FakeTimeProvider"/> fires only timers that already exist when the clock is advanced, and it exposes no way
/// to ask whether any are pending. <c>Task.Delay(TimeSpan, TimeProvider, CancellationToken)</c> goes through
/// <see cref="CreateTimer"/>, so intercepting that is an exact signal rather than a guess.
/// </para>
/// <para>
/// Multi-shot: a sweep loop creates a timer at the top of every iteration, so awaiting the next creation also means the
/// previous iteration has finished its work. Three older tests keep private copies of this
/// (<c>ConversationBudgetRetentionServiceTests</c>, <c>ToolResultRetentionServiceTests</c>,
/// <c>ScheduleDispatchBackgroundServiceTests</c>); the #901 retention sweeps use this shared one.
/// </para>
/// </remarks>
internal sealed class ObservableFakeClock(DateTimeOffset start) : FakeTimeProvider(start)
{
    private TaskCompletionSource _next = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Completes when the next timer is created. Capture this <em>before</em> advancing the clock; capturing it afterwards
    /// can miss a creation that already happened.
    /// </summary>
    public Task NextTimer => Volatile.Read(ref _next).Task;

    /// <inheritdoc />
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = base.CreateTimer(callback, state, dueTime, period);

        // Install the replacement first, then complete the one callers hold, so a continuation that immediately asks for
        // the next signal cannot get the one just fired.
        Interlocked.Exchange(ref _next, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
            .TrySetResult();

        return timer;
    }
}
