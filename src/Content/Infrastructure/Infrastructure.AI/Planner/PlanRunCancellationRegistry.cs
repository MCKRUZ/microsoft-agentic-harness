using Application.AI.Common.Interfaces.Planner;
using Domain.AI.Planner;

namespace Infrastructure.AI.Planner;

/// <summary>
/// In-memory <see cref="IPlanRunCancellationRegistry"/>. Holds one entry per in-flight run, keyed
/// by plan, so a cancel request can signal a running plan without waiting on the per-plan execution
/// lock.
/// </summary>
/// <remarks>
/// <para>
/// <b>Locking.</b> The private gate guards the index only. Cancellation and disposal happen outside
/// it, on a snapshot, because <see cref="CancellationTokenSource.Cancel"/> runs its registered
/// callbacks synchronously: holding a lock across it would put arbitrary continuation code —
/// including a run's own release path — under this type's lock. Each
/// <see cref="PlanRunCancellationRegistration"/> serializes its own cancel-versus-dispose pair
/// internally, so dropping the index lock before signalling is safe.
/// </para>
/// <para>
/// <b>Scope.</b> This registry is deliberately process-local. A multi-instance deployment cancels
/// only runs hosted by the instance that receives the request; cancelling across instances needs a
/// distributed signal (the persisted state rewrite in
/// <see cref="IPlanExecutor.CancelAsync"/> still applies everywhere, so a run on another instance
/// is stopped at its next checkpoint rather than immediately).
/// </para>
/// </remarks>
public sealed class PlanRunCancellationRegistry : IPlanRunCancellationRegistry
{
    private readonly Dictionary<PlanId, List<PlanRunCancellationRegistration>> _runs = [];
    private readonly object _gate = new();

    /// <summary>
    /// The run currently executing on this async flow, used to give a nested run its parent.
    /// <see cref="AsyncLocal{T}"/> flows into the step tasks a run spawns and onward into any
    /// sub-plan those steps invoke, which is exactly the nesting relationship being captured — and
    /// it flows without the intervening step executor having to forward anything, so a nesting path
    /// cannot be added later that forgets to participate.
    /// </summary>
    private readonly AsyncLocal<AmbientRun?> _ambientRun = new();

    /// <inheritdoc />
    public PlanRunCancellationRegistration Register(PlanId planId)
    {
        var parent = _ambientRun.Value;
        var registration = new PlanRunCancellationRegistration(
            this, planId, parent?.Registration.Token ?? default);

        // Published before returning, so a sub-plan started by this run registers beneath it.
        // Writing an AsyncLocal affects this flow and the flows it spawns, never the caller's, so
        // sibling runs on other flows are unaffected.
        _ambientRun.Value = new AmbientRun(registration, parent);

        lock (_gate)
        {
            if (!_runs.TryGetValue(planId, out var registrations))
            {
                registrations = [];
                _runs[planId] = registrations;
            }

            registrations.Add(registration);
        }

        return registration;
    }

    /// <inheritdoc />
    public bool TryCancel(PlanId planId)
    {
        PlanRunCancellationRegistration[] snapshot;
        lock (_gate)
        {
            if (!_runs.TryGetValue(planId, out var registrations) || registrations.Count == 0)
                return false;

            snapshot = [.. registrations];
        }

        var signalled = false;
        foreach (var registration in snapshot)
        {
            // A registration released between the snapshot and here returns false: its run had
            // already finished, so there was nothing left to stop.
            signalled |= registration.SignalCancellation();
        }

        return signalled;
    }

    /// <inheritdoc />
    public void Release(PlanRunCancellationRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);

        lock (_gate)
        {
            if (_runs.TryGetValue(registration.PlanId, out var registrations))
            {
                registrations.Remove(registration);
                if (registrations.Count == 0)
                    _runs.Remove(registration.PlanId);
            }
        }

        // Pop the ambient run, but only when this registration is the one on top of THIS flow.
        // Release normally runs on the same flow as Register (the run disposes its own handle), so
        // the pop restores the parent and a subsequent sibling run on the same flow is not treated
        // as nested. The identity check keeps a Release called from an unrelated flow from
        // corrupting that flow's ambient state.
        if (ReferenceEquals(_ambientRun.Value?.Registration, registration))
            _ambientRun.Value = _ambientRun.Value!.Parent;

        // Outside the gate, and only once the registration is unreachable from the index, so no
        // TryCancel can start signalling it after this point.
        registration.CompleteRelease();
    }

    /// <summary>
    /// One frame of the run-nesting stack for an async flow: the run executing on it and the run
    /// that invoked it. Held by <see cref="AsyncLocal{T}"/>, so each flow sees its own chain.
    /// </summary>
    private sealed record AmbientRun(PlanRunCancellationRegistration Registration, AmbientRun? Parent);
}
