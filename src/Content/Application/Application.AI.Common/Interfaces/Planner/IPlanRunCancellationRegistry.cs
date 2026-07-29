using Domain.AI.Planner;

namespace Application.AI.Common.Interfaces.Planner;

/// <summary>
/// Process-wide index of in-flight plan runs and their cooperative-cancellation tokens. This is the
/// signalling path that makes <see cref="IPlanExecutor.CancelAsync"/> actually stop work rather
/// than merely rewriting persisted state after the fact.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> <see cref="IPlanExecutor"/> serializes per-plan work behind a
/// process-wide lock that the executing run holds for its entire duration. Without a registry, a
/// cancel request could only queue behind that lock, so it would wait for the run it is trying to
/// stop and then rewrite state that the finished run had already written. The registry is a
/// separate, non-blocking structure: <see cref="TryCancel"/> takes no plan lock and performs no
/// I/O, so it can signal a run that is mid-flight.
/// </para>
/// <para>
/// <b>Lifetime.</b> Implementations must be registered as a singleton. <see cref="IPlanExecutor"/>
/// is scoped, and a cancel request arrives on a different scope (and usually a different thread)
/// from the run it targets; a scoped registry would hand the two callers different indexes and
/// silently cancel nothing.
/// </para>
/// <para>
/// <b>Concurrent runs of the same plan.</b> Registrations are tracked per run, not per plan, so a
/// second run queued behind the per-plan lock has its own entry. <see cref="TryCancel"/> signals
/// every live run for the plan; <see cref="Release"/> removes exactly the registration passed to
/// it. Releasing by plan identifier alone would let a finishing run tear down a queued run's
/// registration.
/// </para>
/// </remarks>
public interface IPlanRunCancellationRegistry
{
    /// <summary>
    /// Registers a starting run and returns its cancellation handle. The caller links
    /// <see cref="PlanRunCancellationRegistration.Token"/> into the token it passes to
    /// <see cref="IPlanExecutor.ExecuteAsync(PlanId, CancellationToken)"/> and disposes the handle
    /// when the run ends.
    /// </summary>
    /// <remarks>
    /// <b>Nested runs inherit cancellation.</b> A run started while another run is executing — a
    /// sub-plan invoked by a step — is registered beneath it, and cancelling the outer run cancels
    /// it too. The nesting is detected from the execution flow, so an intermediate step executor
    /// neither forwards anything nor needs to know this exists. That matters: a sub-plan registers
    /// under the <i>child</i> plan's identifier, so <see cref="TryCancel"/> on the parent would
    /// otherwise never reach it, and the child's interrupted step would be recorded as a failure
    /// rather than a cancellation — leaving the plan unresumable.
    /// </remarks>
    /// <param name="planId">The plan being run.</param>
    /// <returns>A live registration; dispose it to release.</returns>
    PlanRunCancellationRegistration Register(PlanId planId);

    /// <summary>
    /// Signals every live run of <paramref name="planId"/>. Non-blocking: acquires no plan lock,
    /// performs no I/O, and returns as soon as the tokens are signalled. Idempotent — a second
    /// call against an already-cancelled run signals an already-signalled token.
    /// </summary>
    /// <param name="planId">The plan to cancel.</param>
    /// <returns>
    /// <see langword="true"/> when at least one live run was signalled; <see langword="false"/>
    /// when no run of that plan is in flight.
    /// </returns>
    bool TryCancel(PlanId planId);

    /// <summary>
    /// Removes <paramref name="registration"/> from the index and disposes its token source.
    /// Idempotent. Prefer disposing the registration — <see cref="PlanRunCancellationRegistration.Dispose"/>
    /// calls straight through to here, which is what keeps registration and release symmetric.
    /// </summary>
    /// <param name="registration">The registration returned by <see cref="Register"/>.</param>
    void Release(PlanRunCancellationRegistration registration);
}
