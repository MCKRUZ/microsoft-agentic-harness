using Domain.AI.Bundles;
using Domain.Common;
using MediatR;

namespace Application.AI.Common.CQRS.Workflows.StartRun;

/// <summary>
/// Queues a stored workflow for execution and returns the job identifier the caller polls.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Starting a run is a separate right from submitting a workflow.</strong> Submission stores a
/// definition; this spends the host's model and tool credentials on it. Keeping them apart means a
/// caller that can author work does not automatically get to execute it.
/// </para>
/// <para>
/// <strong><see cref="Envelope"/> is resolved at the transport boundary from the credential that
/// invoked this</strong>, not from anything stored with the workflow. A run therefore executes under
/// the grant of whoever started it, so a workflow authored by a broadly-permitted caller confers
/// nothing on a narrowly-permitted one that runs it later.
/// </para>
/// </remarks>
public sealed record StartWorkflowRunCommand : IRequest<Result<StartWorkflowRunResult>>
{
    /// <summary>Identifier of the stored workflow to run.</summary>
    public required Guid WorkflowId { get; init; }

    /// <summary>Stable identity of the calling principal, resolved from its token.</summary>
    public required string OwnerId { get; init; }

    /// <summary>Tenant of the calling principal, when the host resolves one.</summary>
    public string? TenantId { get; init; }

    /// <summary>The grant this run executes under, resolved from the invoking credential.</summary>
    public required CapabilityEnvelope Envelope { get; init; }
}
