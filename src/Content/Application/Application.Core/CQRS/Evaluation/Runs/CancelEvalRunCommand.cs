using Domain.Common;
using MediatR;

namespace Application.Core.CQRS.Evaluation.Runs;

/// <summary>Stops an evaluation run the caller started, if it has not begun executing.</summary>
public sealed record CancelEvalRunCommand : IRequest<Result<CancelEvalRunResult>>
{
    /// <summary>The run to stop.</summary>
    public required string JobId { get; init; }

    /// <summary>Stable identity of the calling principal.</summary>
    public required string OwnerId { get; init; }

    /// <summary>Tenant of the calling principal, when the host resolves one.</summary>
    public string? TenantId { get; init; }
}

/// <summary>What a cancellation achieved, as the caller sees it.</summary>
public sealed record CancelEvalRunResult
{
    /// <summary>
    /// Whether the run had stopped by the time this was answered.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>False means the run is executing and will continue.</strong> This is weaker than the
    /// workflow path's answer, and the difference is real rather than an omission: a workflow executing
    /// through the planner can be signalled through <c>IPlanRunCancellationRegistry</c>, whereas an
    /// evaluation in flight is a suite of agent turns with no equivalent registry to signal. Cancelling
    /// a queued or already-finished evaluation is exact; cancelling one mid-suite is not offered rather
    /// than being offered and quietly not honoured.
    /// </para>
    /// <para>
    /// The practical bound on a runaway suite is therefore the configured
    /// <c>MaxCaseExecutionsPerRun</c>, which is applied before any case runs — the spend is capped at
    /// admission rather than interrupted afterwards.
    /// </para>
    /// </remarks>
    public required bool Stopped { get; init; }
}
