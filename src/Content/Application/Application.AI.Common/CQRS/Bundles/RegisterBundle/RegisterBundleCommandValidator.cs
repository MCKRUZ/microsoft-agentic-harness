using FluentValidation;

namespace Application.AI.Common.CQRS.Bundles.RegisterBundle;

/// <summary>
/// Validates <see cref="RegisterBundleCommand"/> before the handler runs. The archive's size and shape are
/// enforced by the staging service against configured limits (they need the config the validator does not
/// have); this only ensures a readable stream was supplied.
/// </summary>
public sealed class RegisterBundleCommandValidator : AbstractValidator<RegisterBundleCommand>
{
    /// <summary>Initializes validation rules.</summary>
    public RegisterBundleCommandValidator()
    {
        RuleFor(x => x.Archive)
            .NotNull().WithMessage("Archive stream is required.")
            .Must(s => s is null || s.CanRead).WithMessage("Archive stream must be readable.");
    }
}
