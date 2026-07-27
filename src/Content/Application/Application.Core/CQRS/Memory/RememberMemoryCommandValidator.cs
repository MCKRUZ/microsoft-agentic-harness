using FluentValidation;

namespace Application.Core.CQRS.Memory;

/// <summary>
/// Validates <see cref="RememberMemoryCommand"/>: key non-empty, bounded, and restricted to the
/// node-id-safe charset (see <see cref="MemoryValidationRules.KeyPattern"/> — <c>':'</c> is the
/// namespace delimiter of the persisted node id and must never appear in a caller key); content
/// non-empty and capped at <see cref="MemoryValidationRules.MaxContentLength"/>; entity type
/// well-formed.
/// </summary>
public sealed class RememberMemoryCommandValidator : AbstractValidator<RememberMemoryCommand>
{
    /// <summary>Initializes validation rules.</summary>
    public RememberMemoryCommandValidator()
    {
        RuleFor(x => x.Key)
            .NotEmpty().WithMessage("Key must not be empty.")
            .MaximumLength(MemoryValidationRules.MaxKeyLength)
                .WithMessage($"Key must not exceed {MemoryValidationRules.MaxKeyLength} characters.")
            .Matches(MemoryValidationRules.KeyPattern)
                .WithMessage("Key may only contain letters, digits, '.', '_' and '-', starting with a letter or digit.");

        RuleFor(x => x.Content)
            .NotEmpty().WithMessage("Content must not be empty.")
            .MaximumLength(MemoryValidationRules.MaxContentLength)
                .WithMessage($"Content must not exceed {MemoryValidationRules.MaxContentLength} characters.");

        RuleFor(x => x.EntityType)
            .NotEmpty().WithMessage("EntityType must not be empty.")
            .MaximumLength(MemoryValidationRules.MaxEntityTypeLength)
                .WithMessage($"EntityType must not exceed {MemoryValidationRules.MaxEntityTypeLength} characters.")
            .Matches(MemoryValidationRules.EntityTypePattern)
                .WithMessage("EntityType may only contain letters, digits, '_' and '-', starting with a letter.");
    }
}
