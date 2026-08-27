namespace Infrastructure.AI.Tests.Escalation;

/// <summary>
/// The budget an escalation test allows for work the service finishes on a background
/// continuation — a timeout firing, a pending registration landing, a reconciler tick — when the
/// property under test is <em>that</em> the work happened, never how quickly.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this exists (#537).</strong> Six polling loops in this folder each waited under a
/// bare wall-clock literal, and those literals had drifted to 5s, 10s and 20s with no stated reason
/// for the differences. Every one is the same shape: spin until something non-null appears, give up
/// at a fixed deadline. None of them asserts a duration. What they actually wait on is a
/// continuation that writes to a SQLite file a sibling store still holds open and then calls through
/// to a mock — work whose cost depends on what else the machine is doing, not on the code under
/// test. Under suite load the short ones lose, and the test fails with a cancellation that reads
/// like a functional defect rather than the contention it is.
/// </para>
/// <para>
/// A single named budget rather than a shared loop: the six loops genuinely differ — two advance a
/// fake clock on each iteration, they poll three different accessors, and their delays are tuned
/// differently. The <em>budget</em> is the only part that had drifted, so the budget is the part
/// worth naming. Generous enough that contention cannot reach it, bounded so genuinely stuck work
/// still fails the run instead of hanging it.
/// </para>
/// <para>
/// <strong>Not for a test whose subject is the deadline.</strong>
/// <c>DefaultEscalationServiceTests.Timeout_CallerCancelled_PropagatesCancellation</c> cancels after
/// 100ms deliberately — there the short budget is the thing being proved, and it is left alone.
/// <c>SandboxTestDeadlines.Generous</c> is the same idea for a different mechanism (a real OS
/// subprocess); the two are kept apart because they are independent decisions that happen to share
/// a number today, and coupling them would make a change to one silently move the other.
/// </para>
/// </remarks>
internal static class EscalationTestDeadlines
{
    /// <summary>
    /// How long to wait for a background continuation whose duration is incidental to the assertion.
    /// </summary>
    public static readonly TimeSpan BackgroundWork = TimeSpan.FromSeconds(60);
}
