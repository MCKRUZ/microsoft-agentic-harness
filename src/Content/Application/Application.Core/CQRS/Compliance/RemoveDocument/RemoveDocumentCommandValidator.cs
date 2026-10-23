using Application.Core.Compliance;
using FluentValidation;

namespace Application.Core.CQRS.Compliance.RemoveDocument;

/// <summary>
/// Validates <see cref="RemoveDocumentCommand"/>: a document id in the form ingestion gives it, and an operator.
/// </summary>
/// <remarks>
/// Anything else either matches no document, and would still produce a receipt, or matches ids that are not documents'
/// at all; it is refused here so the operator learns the id was wrong.
/// </remarks>
public sealed class RemoveDocumentCommandValidator : AbstractValidator<RemoveDocumentCommand>
{
    /// <summary>Initializes a new instance of the <see cref="RemoveDocumentCommandValidator"/> class.</summary>
    public RemoveDocumentCommandValidator()
    {
        RuleFor(x => x.DocumentId)
            .Must(AuditedDocumentRemoval.IsDocumentId)
            .WithMessage("DocumentId must be 16 upper-case hexadecimal digits, as ingestion forms it.");
        RuleFor(x => x.OperatorId).NotEmpty();
    }
}
