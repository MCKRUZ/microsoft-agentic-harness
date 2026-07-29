using Domain.Common;
using MediatR;

namespace Application.AI.Common.CQRS.Workflows.Submit;

/// <summary>
/// Submits an externally-authored workflow definition, validates it against the host's admission
/// caps, and persists it as an owner-scoped plan the caller can later run.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Ownership comes from the caller's ambient scope, never from this command.</strong> There is
/// deliberately no owner or tenant property here. The transport establishes identity from the caller's
/// token before the request reaches MediatR, and the plan store stamps ownership from that ambient
/// scope. A command carrying an owner field would be a field an attacker could set — and this repo has
/// already had three separate defects where a request reached persistence with no established scope
/// and was stored as a world-readable global record.
/// </para>
/// <para>
/// Submission does not run anything. It returns the identifier of a stored workflow; starting it is a
/// separate, separately-authorized operation. Keeping the two apart means a caller who can author a
/// workflow does not automatically have the right to spend the host's credentials executing it.
/// </para>
/// </remarks>
public sealed record SubmitWorkflowCommand : IRequest<Result<SubmitWorkflowResult>>
{
    /// <summary>The workflow to admit and store.</summary>
    public required WorkflowDefinition Definition { get; init; }
}
