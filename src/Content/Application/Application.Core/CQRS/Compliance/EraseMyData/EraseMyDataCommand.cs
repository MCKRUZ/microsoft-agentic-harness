using Application.Common.Interfaces.MediatR;
using Domain.AI.KnowledgeGraph.Models;
using Domain.Common;
using MediatR;

namespace Application.Core.CQRS.Compliance.EraseMyData;

/// <summary>
/// Right-to-erasure request that deletes <em>only the calling user's own</em> knowledge —
/// graph nodes/edges, feedback weights, and vector embeddings — across every storage layer.
/// </summary>
/// <remarks>
/// <para>
/// This command carries <b>no target-owner parameter by design</b>. The subject of the erasure
/// is always the authenticated caller, derived by the handler from the ambient
/// <see cref="Application.AI.Common.Interfaces.KnowledgeGraph.IKnowledgeScope"/> (established at the
/// authenticated entry point from the caller's identity claim). There is deliberately no way for a
/// caller to direct this at another owner's data — that would be a horizontal-privilege
/// (IDOR) escalation. An admin "erase arbitrary owner" capability is intentionally out of scope and
/// must be built as a separate, role-gated command if ever required.
/// </para>
/// <para>
/// Implements <see cref="IAuditable"/> so the <c>AuditTrailBehavior</c> records this destructive
/// compliance action (who / when / outcome) even when it is denied or fails. The handler additionally
/// logs the erasure receipt with structured detail.
/// </para>
/// </remarks>
public sealed record EraseMyDataCommand : IRequest<Result<ErasureReceipt>>, IAuditable
{
    /// <inheritdoc />
    /// <remarks>Stable, non-sensitive action name for the compliance audit trail.</remarks>
    public string AuditAction => "RightToErasure.EraseMyData";
}
