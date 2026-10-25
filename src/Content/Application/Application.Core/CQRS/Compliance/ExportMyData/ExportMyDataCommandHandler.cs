using Application.AI.Common.Interfaces.KnowledgeGraph;
using Application.Core.Compliance;
using Domain.Common;
using Domain.Common.Compliance;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Application.Core.CQRS.Compliance.ExportMyData;

/// <summary>
/// Fulfils a self-scoped request for a copy of one's own data. Reads the caller's identity from the ambient
/// <see cref="IKnowledgeScope"/> and copies only that person's data through <see cref="AuditedExport"/>.
/// </summary>
/// <remarks>
/// When no authenticated user scope is present the handler fails closed with <see cref="Result{T}.Forbidden(string)"/>,
/// reads nothing, and records the denial: a check that holds even if the entry point's authorization were misconfigured.
/// Nothing here logs the caller's id.
/// </remarks>
public sealed class ExportMyDataCommandHandler
    : IRequestHandler<ExportMyDataCommand, Result<PersonalDataExport>>
{
    private const string NoUserScope = "A copy of your data requires an authenticated user; no user scope is present.";

    private readonly IKnowledgeScope _scope;
    private readonly AuditedExport _export;
    private readonly ILogger<ExportMyDataCommandHandler> _logger;

    /// <summary>Initializes a new instance of the <see cref="ExportMyDataCommandHandler"/> class.</summary>
    /// <param name="scope">Ambient knowledge scope supplying the authenticated caller's identity.</param>
    /// <param name="export">Makes the copy and writes its audit record.</param>
    /// <param name="logger">Logger for refused requests.</param>
    public ExportMyDataCommandHandler(IKnowledgeScope scope, AuditedExport export, ILogger<ExportMyDataCommandHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(export);
        ArgumentNullException.ThrowIfNull(logger);

        _scope = scope;
        _export = export;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result<PersonalDataExport>> Handle(ExportMyDataCommand request, CancellationToken cancellationToken)
    {
        var ownerId = _scope.UserId;
        if (string.IsNullOrWhiteSpace(ownerId))
        {
            _logger.LogWarning("Personal-data export request rejected: no authenticated user scope present.");
            await _export.RecordDeniedAsync(ErasureActor.DataSubject, nameof(ExportMyDataCommand), NoUserScope);
            return Result<PersonalDataExport>.Forbidden(NoUserScope);
        }

        return await _export.ExportAsync(ownerId, ErasureActor.DataSubject, nameof(ExportMyDataCommand), cancellationToken);
    }
}
