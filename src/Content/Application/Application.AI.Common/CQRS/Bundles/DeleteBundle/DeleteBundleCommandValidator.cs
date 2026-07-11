using FluentValidation;

namespace Application.AI.Common.CQRS.Bundles.DeleteBundle;

/// <summary>
/// Validates <see cref="DeleteBundleCommand"/> before the handler runs.
/// </summary>
public sealed class DeleteBundleCommandValidator : AbstractValidator<DeleteBundleCommand>
{
    /// <summary>Initializes validation rules.</summary>
    public DeleteBundleCommandValidator()
    {
        RuleFor(x => x.Handle)
            .NotEmpty().WithMessage("Handle is required.");
    }
}
