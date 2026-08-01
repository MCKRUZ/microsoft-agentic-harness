using Application.AI.Common.Evaluation.Models;
using Domain.AI.Bundles;
using Domain.Common;
using MediatR;

namespace Application.Core.CQRS.Evaluation.Runs;

/// <summary>
/// Accepts an evaluation run and queues it, returning the identifier to poll. Nothing is evaluated on
/// the calling thread.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Datasets are named, never pathed.</strong> Names mean something inside the host's configured
/// dataset roots and are resolved server-side. This is why the command carries no path: a path on the
/// wire would make every request a filesystem reference, and the only thing between that and an
/// arbitrary read would be a guard remembering to refuse. The guard still runs underneath — but the
/// attack is unsayable here rather than merely rejected there.
/// </para>
/// <para>
/// <strong>Ownership and the envelope come from the transport, never from the request body.</strong>
/// There is no field a caller could use to nominate a different owner or a wider grant. Both are
/// captured on the run record because a run outlives the request that started it and executes on a
/// thread with no caller attached.
/// </para>
/// </remarks>
public sealed record StartEvalRunCommand : IRequest<Result<StartEvalRunResult>>
{
    /// <summary>Names of the datasets to evaluate, as this host publishes them.</summary>
    public required IReadOnlyList<string> DatasetNames { get; init; }

    /// <summary>How the run executes. Bounded by the configured ceilings before it is accepted.</summary>
    public EvalRunOptions Options { get; init; } = new();

    /// <summary>Stable identity of the calling principal, resolved at the transport boundary.</summary>
    public required string OwnerId { get; init; }

    /// <summary>Tenant of the calling principal, when the host resolves one.</summary>
    public string? TenantId { get; init; }

    /// <summary>
    /// The grant the run executes under, resolved from the credential that made <em>this</em> request.
    /// </summary>
    /// <remarks>
    /// Every evaluation case is a governed agent turn that can invoke tools, so an evaluation run needs
    /// an envelope for the same reason a workflow run does. Resolved per request rather than stored
    /// with anything, so a suite authored under a broad grant confers nothing on a caller who runs it
    /// later under a narrow one.
    /// </remarks>
    public required CapabilityEnvelope Envelope { get; init; }
}
