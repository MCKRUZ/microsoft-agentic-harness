using Application.Common.Interfaces.MediatR;
using Application.Core.Compliance;
using Domain.AI.KnowledgeGraph.Models;
using Domain.Common;
using MediatR;

namespace Application.Core.CQRS.Compliance.EraseMyData;

/// <summary>
/// Right-to-erasure request that deletes <em>only the calling user's own</em> data, across the knowledge graph and every
/// registered store (#900).
/// </summary>
/// <remarks>
/// <para>
/// This command carries <b>no target-owner parameter by design</b>. The subject of the erasure
/// is always the authenticated caller, derived by the handler from the ambient
/// <see cref="Application.AI.Common.Interfaces.KnowledgeGraph.IKnowledgeScope"/> (established at the
/// authenticated entry point from the caller's identity claim). There is deliberately no way for a
/// caller to direct this at another owner's data — that would be a horizontal-privilege
/// (IDOR) escalation. Erasing someone else on their request is the separate, role-gated
/// <c>EraseSubjectDataCommand</c>.
/// </para>
/// <para>
/// Audited by <see cref="Application.Core.Compliance.AuditedErasure"/>, not by <c>IAuditable</c>: the generic audit
/// behaviour records an entry per request with no way to say who the subject was without naming them, while the erasure
/// record carries a pseudonymous reference and per-store counts, and records denials too.
/// </para>
/// </remarks>
public sealed record EraseMyDataCommand : IRequest<Result<ErasureReceipt>>, IHasTimeout
{
    /// <inheritdoc />
    public TimeSpan? Timeout => AuditedErasure.RequestTimeout;
}
