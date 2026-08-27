namespace Infrastructure.AI.Tests.Sandbox;

/// <summary>
/// The deadline a sandbox test gives a real OS subprocess when the property under test is
/// <em>what</em> the sandbox produced — an exit code, bound output, a signed attestation, a
/// cleaned-up workspace — rather than how fast it produced it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this exists (#537).</strong> These tests spawn actual processes and then wait on
/// them under a fixed wall-clock deadline. Every such deadline was written as a bare literal, and
/// they had drifted to two different values (10s in the executor and isolation tests, 5s in the
/// session-factory ones) for no stated reason. Each was comfortable in isolation — the whole
/// sandbox suite finishes in about three seconds on an idle machine — and each was a race the test
/// simply expected to win. Run alongside the other ~3,300 tests in this assembly, they lost it
/// often enough that roughly one full-assembly run in seven failed, on a different member of the
/// family each time. A run that fails for a reason unrelated to the code under test teaches
/// whoever hits it to re-run until green, which is precisely the habit that lets a real regression
/// through — and the local push gate will not write its receipt without a clean pass, so the
/// flake intermittently blocked the whole review-and-push workflow.
/// </para>
/// <para>
/// <strong>Why generous rather than tuned.</strong> Nothing here is asserting a duration, so there
/// is no signal to preserve by keeping the number tight — a slow-but-correct subprocess is still
/// correct, and this value only decides how long the suite waits before calling a genuinely hung
/// process hung. It is deliberately far above the observed cost (sub-second) so that scheduling
/// contention cannot reach it, and still bounded so a real hang fails the run rather than
/// wedging it.
/// </para>
/// <para>
/// <strong>Do not use this for a test whose subject IS the deadline.</strong>
/// <c>ProcessSandboxExecutorTests.ExecuteAsync_Timeout_KillsProcessAndReturnsFail</c> passes its
/// own short timeout against a command that runs for a minute; widening that one would delete the
/// behaviour it exists to prove. The distinction is the whole point: a timeout test asserts the
/// deadline fires, and every other test here merely needs one that never does.
/// </para>
/// </remarks>
internal static class SandboxTestDeadlines
{
    /// <summary>
    /// Deadline for a subprocess whose duration is incidental to the assertion.
    /// </summary>
    public static readonly TimeSpan Generous = TimeSpan.FromSeconds(60);
}
