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

    /// <summary>
    /// The submitting caller's approver identity, resolved by the transport from its token — never
    /// from the request body. Null when the host could not establish one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Present only so a caller cannot author a gate that it may then approve itself, which would make
    /// the gate ceremonial. It is not ownership: ownership still comes from the ambient scope and has
    /// no field here. It is a <em>different</em> identity from the owner, resolved through a different,
    /// operator-chosen claim — comparing an owner id against a roster of sign-in names would silently
    /// never match, and the self-approval check would pass for everyone.
    /// </para>
    /// <para>
    /// A null value refuses human gates rather than skipping the check. That is the whole reason this
    /// is safe to carry on a command: a transport that forgets to populate it — or a principal that
    /// carries no usable claim — loses the ability to submit gates, it does not gain the ability to
    /// self-approve.
    /// </para>
    /// </remarks>
    public string? SubmitterApproverName { get; init; }
}
