using Application.Core.CQRS.Compliance.EraseSubjectData;
using FluentValidation;

namespace Application.Core.CQRS.Compliance.ExportSubjectData;

/// <summary>
/// Validates <see cref="ExportSubjectDataCommand"/> with the same subject rules as operator erasure: a person is named the
/// same way whichever right they exercise.
/// </summary>
/// <remarks>
/// The id must be given exactly as the identity provider issues it. Conversations and document chunks match their owner
/// exactly, so a re-cased or padded id would read as a person with no data, and the copy would be empty while looking
/// complete.
/// </remarks>
public sealed class ExportSubjectDataCommandValidator : AbstractValidator<ExportSubjectDataCommand>
{
    /// <summary>Initializes the validator.</summary>
    public ExportSubjectDataCommandValidator()
    {
        RuleFor(x => x.SubjectId)
            .Cascade(CascadeMode.Stop)
            .NotEmpty()
            .MaximumLength(EraseSubjectDataCommandValidator.MaxSubjectIdLength)
            .Must(id => id == id.Trim())
            .WithMessage("SubjectId must be given exactly as the identity provider issues it, without surrounding whitespace.");

        RuleFor(x => x.OperatorId).NotEmpty();
    }
}
