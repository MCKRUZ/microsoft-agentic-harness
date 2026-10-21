using FluentValidation;

namespace Application.Core.CQRS.Compliance.EraseSubjectData;

/// <summary>
/// Validates <see cref="EraseSubjectDataCommand"/>: both ids present, and the subject id free of surrounding whitespace
/// and of a sane length.
/// </summary>
/// <remarks>
/// A blank subject would reach no one and still produce a receipt; a padded one would miss every store that matches the
/// owner exactly, such as conversations, while the stores that canonicalize it erased the person. Both are refused here
/// rather than repaired, so the operator learns the id was wrong.
/// </remarks>
public sealed class EraseSubjectDataCommandValidator : AbstractValidator<EraseSubjectDataCommand>
{
    /// <summary>The longest subject id accepted. Identity-provider ids are far shorter; this bounds a pasted mistake.</summary>
    public const int MaxSubjectIdLength = 256;

    /// <summary>Initializes a new instance of the <see cref="EraseSubjectDataCommandValidator"/> class.</summary>
    public EraseSubjectDataCommandValidator()
    {
        RuleFor(x => x.SubjectId)
            .Cascade(CascadeMode.Stop)
            .NotEmpty()
            .MaximumLength(MaxSubjectIdLength)
            .Must(id => id == id.Trim())
            .WithMessage("SubjectId must be given exactly as the identity provider issues it, without surrounding whitespace.");

        RuleFor(x => x.OperatorId).NotEmpty();
    }
}
