using Application.Common.Interfaces.MediatR;
using Application.Core.Compliance;
using Domain.Common;
using Domain.Common.Compliance;
using MediatR;

namespace Application.Core.CQRS.Compliance.ExportMyData;

/// <summary>
/// Right of access and portability (GDPR Art. 15 and 20): a machine-readable copy of <em>only the calling user's own</em>
/// data, from every registered store, with a manifest of what each returned (#902).
/// </summary>
/// <remarks>
/// <para>
/// Carries <b>no target-owner parameter by design</b>: the subject is always the authenticated caller, taken by the handler
/// from the ambient <see cref="Application.AI.Common.Interfaces.KnowledgeGraph.IKnowledgeScope"/>. There is no way to
/// direct it at someone else's data.
/// </para>
/// <para>
/// A command rather than a query although it changes nothing: it writes an audit record, and must never be served from a
/// cache, since a cached copy would outlive the data it copies.
/// </para>
/// </remarks>
public sealed record ExportMyDataCommand : IRequest<Result<PersonalDataExport>>, IHasTimeout
{
    /// <inheritdoc />
    public TimeSpan? Timeout => AuditedExport.RequestTimeout;
}
