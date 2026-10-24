using Application.Common.Interfaces.MediatR;
using Application.Core.Compliance;
using Domain.AI.KnowledgeGraph.Models;
using Domain.Common;
using MediatR;

namespace Application.Core.CQRS.Compliance.EraseSubjectData;

/// <summary>
/// Operator-initiated right-to-erasure: erases a named person's data on their request, for someone who cannot sign in to
/// erase it themselves (#900).
/// </summary>
/// <remarks>
/// <para>
/// <b>Authorization lives at the entry point.</b> The only sender is <c>ComplianceController</c>, which requires the
/// dedicated erasure role and a tenant listed in <c>AppConfig:AI:Erasure:OperatorTenantIds</c>, refuses while sign-in is
/// bypassed, and records every refusal; the same entry-point pattern as every other operator command in this harness
/// (<c>RefreshSkillRegistryCommand</c>, <c>RefreshAgentRegistryCommand</c>). The MediatR role behaviour is not
/// used: the template's <c>IIdentityService</c> is a stub that grants no role. This command must never be exposed as an
/// agent-callable tool: an erasure is decided by an accountable person, never by an agent turn.
/// </para>
/// <para>
/// <b>The subject id is taken exactly as given.</b> Conversations match their owner exactly, so a trimmed or re-cased id
/// would silently miss them; surrounding whitespace is rejected rather than trimmed. Supply the id as the person's
/// identity provider issues it (the <c>oid</c> claim for Microsoft Entra ID).
/// </para>
/// </remarks>
public sealed record EraseSubjectDataCommand : IRequest<Result<ErasureReceipt>>, IHasTimeout
{
    /// <inheritdoc />
    public TimeSpan? Timeout => AuditedErasure.RequestTimeout;

    /// <summary>Gets the id of the person to erase, exactly as their identity provider issues it.</summary>
    public required string SubjectId { get; init; }

    /// <summary>Gets the id of the operator asking, from their authenticated identity. Recorded in the audit trail.</summary>
    public required string OperatorId { get; init; }
}
