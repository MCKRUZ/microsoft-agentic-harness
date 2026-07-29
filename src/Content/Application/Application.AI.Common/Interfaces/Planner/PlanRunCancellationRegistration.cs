using Domain.AI.Planner;

namespace Application.AI.Common.Interfaces.Planner;

/// <summary>
/// A single in-flight plan run's cooperative-cancellation handle, handed out by
/// <see cref="IPlanRunCancellationRegistry.Register"/>. The run links <see cref="Token"/> into the
/// token it passes down its own call tree; a concurrent
/// <see cref="IPlanRunCancellationRegistry.TryCancel"/> signals that token, which is what actually
/// stops the work.
/// </summary>
/// <remarks>
/// <para>
/// <b>Symmetric by construction.</b> The handle is <see cref="IDisposable"/> and
/// <see cref="Dispose"/> routes back through the registry that created it, so the only supported
/// release is disposing the object the registering component already holds. A run started with
/// <c>using var registration = registry.Register(planId);</c> cannot leak its registration on any
/// exit path — normal return, failure, or exception — and no other component is in a position to
/// release it on the run's behalf.
/// </para>
/// <para>
/// <b>Thread safety.</b> <see cref="SignalCancellation"/> and <see cref="CompleteRelease"/> are
/// mutually exclusive under a private gate, so the underlying
/// <see cref="CancellationTokenSource"/> is never cancelled and disposed concurrently from two
/// threads — the one race that <see cref="CancellationTokenSource"/> does not tolerate. Cancelling a
/// run that has already been released is a no-op rather than an error: the run it would have stopped
/// has already finished, which is precisely why it was released.
/// </para>
/// <para>
/// The gate excludes other <em>threads</em>, not re-entry on the same one. <c>Cancel()</c> runs its
/// registered callbacks inline while the gate is held, and a <c>lock</c> is reentrant, so a callback
/// that unwound a run synchronously all the way back to <c>Dispose()</c> on this thread would re-enter
/// <see cref="CompleteRelease"/> and dispose the source mid-cancel. That cannot happen as the executor
/// is written — the unwind path is asynchronous through several awaits, so the continuation never runs
/// on this stack — but the guarantee above is a property of the caller, not of this lock, and should
/// not be relied on by a future caller that cancels from a synchronous continuation.
/// </para>
/// </remarks>
public sealed class PlanRunCancellationRegistration : IDisposable
{
    private readonly IPlanRunCancellationRegistry _registry;
    private readonly CancellationTokenSource _source;
    private readonly object _gate = new();
    private bool _released;

    /// <summary>
    /// Initializes a new registration owned by <paramref name="registry"/>. Constructed by the
    /// registry itself — callers obtain instances from
    /// <see cref="IPlanRunCancellationRegistry.Register"/>.
    /// </summary>
    /// <param name="registry">The registry that will remove this registration on release.</param>
    /// <param name="planId">The plan whose run this registration cancels.</param>
    /// <param name="parentRunToken">
    /// The cancellation token of the run that is invoking this one, when this run is nested (a
    /// sub-plan). The registration's source is linked to it, so cancelling an outer run cancels
    /// every run beneath it without the nesting call site having to forward anything. Pass
    /// <see langword="default"/> for a root run.
    /// </param>
    public PlanRunCancellationRegistration(
        IPlanRunCancellationRegistry registry,
        PlanId planId,
        CancellationToken parentRunToken = default)
    {
        _registry = registry;
        PlanId = planId;

        // A linked source when nested, so the framework performs the cascade. A run that starts
        // while its parent is already cancelling gets an already-signalled token, which is correct:
        // it must not begin work the operator has asked to stop.
        _source = parentRunToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(parentRunToken)
            : new CancellationTokenSource();

        Token = _source.Token;
    }

    /// <summary>The plan whose run this registration cancels.</summary>
    public PlanId PlanId { get; }

    /// <summary>
    /// The run's cancellation token. Captured at construction so it stays readable after the
    /// registration is released; linking new sources off it is only valid while the registration is
    /// live, which is the whole of the run's lifetime.
    /// </summary>
    public CancellationToken Token { get; }

    /// <summary>
    /// Signals <see cref="Token"/>. Returns <see langword="false"/> when the registration has
    /// already been released — the run finished before the cancel arrived, so there is nothing left
    /// to stop.
    /// </summary>
    /// <returns><see langword="true"/> when the token was signalled.</returns>
    public bool SignalCancellation()
    {
        lock (_gate)
        {
            if (_released)
                return false;

            _source.Cancel();
            return true;
        }
    }

    /// <summary>
    /// Disposes the underlying token source. Called by the registry <i>after</i> the registration
    /// has been removed from its index, so no further <see cref="SignalCancellation"/> can begin.
    /// Idempotent.
    /// </summary>
    public void CompleteRelease()
    {
        lock (_gate)
        {
            if (_released)
                return;

            _released = true;
            _source.Dispose();
        }
    }

    /// <summary>
    /// Releases this registration through its owning registry. Idempotent, and the only release
    /// path a run should use.
    /// </summary>
    public void Dispose() => _registry.Release(this);
}
