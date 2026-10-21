using Application.Core.Compliance;
using Domain.AI.KnowledgeGraph.Models;
using Domain.Common;
using MediatR;

namespace Application.Core.CQRS.Compliance.EraseSubjectData;

/// <summary>
/// Fulfils an operator-initiated right-to-erasure request through <see cref="AuditedErasure"/>, recording the operator as
/// the actor and the subject only by pseudonymous reference (#900).
/// </summary>
/// <remarks>
/// Uses the same erasure as the self-service path, so the receipt and its completeness mean the same thing whoever asked.
/// The erasure runs system-scoped (see <c>PersonalDataErasureCoordinator</c>), so the operator's own scope does not hide
/// the subject's data.
/// </remarks>
public sealed class EraseSubjectDataCommandHandler
    : IRequestHandler<EraseSubjectDataCommand, Result<ErasureReceipt>>
{
    private readonly AuditedErasure _erasure;

    /// <summary>Initializes a new instance of the <see cref="EraseSubjectDataCommandHandler"/> class.</summary>
    /// <param name="erasure">Runs the erasure and writes its audit record.</param>
    public EraseSubjectDataCommandHandler(AuditedErasure erasure)
    {
        ArgumentNullException.ThrowIfNull(erasure);
        _erasure = erasure;
    }

    /// <inheritdoc />
    public Task<Result<ErasureReceipt>> Handle(EraseSubjectDataCommand request, CancellationToken cancellationToken) =>
        _erasure.EraseAsync(
            request.SubjectId, ErasureActor.Operator(request.OperatorId), nameof(EraseSubjectDataCommand),
            cancellationToken);
}
