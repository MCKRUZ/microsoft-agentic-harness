using FluentValidation;

namespace Application.Core.CQRS.Memory;

/// <summary>
/// Validates <see cref="ForgetMemoryCommand"/> with the same key rules as the write side
/// (<see cref="MemoryValidationRules"/>), so every key that can be written over HTTP can also be
/// forgotten over HTTP. Keys outside this charset (e.g. auto-extracted conversation facts, whose
/// keys contain <c>':'</c>) are not addressable via this surface — they are governed by memory
/// decay and the self-scoped erase-my-data endpoint instead.
/// </summary>
public sealed class ForgetMemoryCommandValidator : AbstractValidator<ForgetMemoryCommand>
{
    /// <summary>Initializes validation rules.</summary>
    public ForgetMemoryCommandValidator()
    {
        RuleFor(x => x.Key)
            .NotEmpty().WithMessage("Key must not be empty.")
            .MaximumLength(MemoryValidationRules.MaxKeyLength)
                .WithMessage($"Key must not exceed {MemoryValidationRules.MaxKeyLength} characters.")
            .Matches(MemoryValidationRules.KeyPattern)
                .WithMessage("Key may only contain letters, digits, '.', '_' and '-', starting with a letter or digit.");
    }
}
