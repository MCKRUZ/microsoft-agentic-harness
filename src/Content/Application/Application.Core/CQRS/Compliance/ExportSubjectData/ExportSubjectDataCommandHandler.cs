using Application.Core.Compliance;
using Domain.Common;
using Domain.Common.Compliance;
using MediatR;

namespace Application.Core.CQRS.Compliance.ExportSubjectData;

/// <summary>
/// Makes a copy of a named person's data for an operator through <see cref="AuditedExport"/>, which records the request
/// against the operator and names the person only by pseudonymous reference.
/// </summary>
public sealed class ExportSubjectDataCommandHandler
    : IRequestHandler<ExportSubjectDataCommand, Result<PersonalDataExport>>
{
    private readonly AuditedExport _export;

    /// <summary>Initializes a new instance of the <see cref="ExportSubjectDataCommandHandler"/> class.</summary>
    /// <param name="export">Makes the copy and writes its audit record.</param>
    public ExportSubjectDataCommandHandler(AuditedExport export)
    {
        ArgumentNullException.ThrowIfNull(export);
        _export = export;
    }

    /// <inheritdoc />
    public Task<Result<PersonalDataExport>> Handle(ExportSubjectDataCommand request, CancellationToken cancellationToken) =>
        _export.ExportAsync(
            request.SubjectId, ErasureActor.Operator(request.OperatorId), nameof(ExportSubjectDataCommand), cancellationToken);
}
