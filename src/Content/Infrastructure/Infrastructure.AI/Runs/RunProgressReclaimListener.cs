using Application.AI.Common.Interfaces.Runs;

namespace Infrastructure.AI.Runs;

/// <summary>
/// Releases the progress broker's bookkeeping for reclaimed runs, through the same seam every other
/// holder of run-scoped state uses.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why an adapter rather than making the broker itself a listener.</strong> Registering a
/// listener that resolves through <c>IRunProgressBroker</c> means a factory descriptor, and
/// <c>TryAddEnumerable</c> refuses those — it distinguishes registrations by implementation type, and a
/// factory has none to offer. A named type is what makes the registration idempotent, which matters
/// because the run substrate is registered with <c>TryAdd</c> throughout and may be composed more than
/// once.
/// </para>
/// <para>
/// It resolves <see cref="IRunProgressBroker"/> rather than the in-memory broker directly, so a host
/// that substituted its own broker has <em>that</em> one released rather than the default it replaced.
/// </para>
/// </remarks>
/// <param name="broker">The broker whose per-run bookkeeping this releases.</param>
public sealed class RunProgressReclaimListener(IRunProgressBroker broker) : IRunReclaimListener
{
    private readonly IRunProgressBroker _broker =
        broker ?? throw new ArgumentNullException(nameof(broker));

    /// <inheritdoc />
    public void OnRunsReclaimed(IReadOnlyList<string> jobIds)
    {
        ArgumentNullException.ThrowIfNull(jobIds);

        foreach (var jobId in jobIds)
            _broker.Forget(jobId);
    }
}
