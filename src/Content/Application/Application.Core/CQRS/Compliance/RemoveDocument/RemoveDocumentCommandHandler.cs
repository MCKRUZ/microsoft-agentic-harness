using Application.Core.Compliance;
using Domain.AI.KnowledgeGraph.Models;
using Domain.Common;
using MediatR;

namespace Application.Core.CQRS.Compliance.RemoveDocument;

/// <summary>
/// Fulfils an operator's request to remove a document through <see cref="AuditedDocumentRemoval"/>, recording the
/// operator as the actor (#901).
/// </summary>
public sealed class RemoveDocumentCommandHandler : IRequestHandler<RemoveDocumentCommand, Result<ErasureReceipt>>
{
    private readonly AuditedDocumentRemoval _removal;

    /// <summary>Initializes a new instance of the <see cref="RemoveDocumentCommandHandler"/> class.</summary>
    /// <param name="removal">Runs the removal and writes its audit record.</param>
    public RemoveDocumentCommandHandler(AuditedDocumentRemoval removal)
    {
        ArgumentNullException.ThrowIfNull(removal);
        _removal = removal;
    }

    /// <inheritdoc />
    public Task<Result<ErasureReceipt>> Handle(RemoveDocumentCommand request, CancellationToken cancellationToken) =>
        _removal.RemoveAsync(request.DocumentId, ErasureActor.Operator(request.OperatorId), cancellationToken);
}
