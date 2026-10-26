using Application.Common.Interfaces.MediatR;
using Application.Core.Compliance;
using Domain.Common;
using Domain.Common.Compliance;
using MediatR;

namespace Application.Core.CQRS.Compliance.ExportSubjectData;

/// <summary>
/// An operator's request for a copy of a named person's data, made on that person's behalf when they cannot sign in to ask
/// for it themselves (#902). Returns the same <see cref="PersonalDataExport"/> the self-service request does.
/// </summary>
/// <remarks>
/// Whether the caller may make it is decided at the entry point, which knows the operator's role and tenant; this command
/// carries only who asked and about whom, so the audit record can name both.
/// </remarks>
public sealed record ExportSubjectDataCommand : IRequest<Result<PersonalDataExport>>, IHasTimeout
{
    /// <inheritdoc />
    public TimeSpan? Timeout => AuditedExport.RequestTimeout;

    /// <summary>The person whose copy is made, exactly as their identity provider issues the id.</summary>
    public required string SubjectId { get; init; }

    /// <summary>The operator making the request, recorded in the audit trail.</summary>
    public required string OperatorId { get; init; }
}
