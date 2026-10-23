using Domain.AI.KnowledgeGraph.Models;
using Domain.Common;
using MediatR;

namespace Application.Core.CQRS.Compliance.RemoveDocument;

/// <summary>
/// Operator-initiated removal of one source document from the corpus, with the knowledge drawn from it (#901).
/// </summary>
/// <remarks>
/// <para>
/// <b>Authorization lives at the entry point</b>, as for <c>EraseSubjectDataCommand</c>: the only sender is
/// <c>ComplianceController</c>, which requires the document-removal role and a tenant listed in
/// <c>AppConfig:AI:Erasure:OperatorTenantIds</c>, refuses while sign-in is bypassed, and records every refusal. This
/// command must never be exposed as an agent-callable tool: removing content is decided by an accountable person.
/// </para>
/// <para>
/// A document id is derived from where the document came from, so the removal reaches every tenant and collection that
/// loaded it.
/// </para>
/// </remarks>
public sealed record RemoveDocumentCommand : IRequest<Result<ErasureReceipt>>
{
    /// <summary>Gets the document to remove: 16 upper-case hex digits, as ingestion forms it.</summary>
    public required string DocumentId { get; init; }

    /// <summary>Gets the id of the operator asking, from their authenticated identity. Recorded in the audit trail.</summary>
    public required string OperatorId { get; init; }
}
