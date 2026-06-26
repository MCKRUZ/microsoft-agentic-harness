using Domain.Common.Config.AI.WorkMemory;
using FluentValidation;

namespace Application.Core.Validation;

/// <summary>
/// Validates <see cref="WorkMemoryConfig"/>: the episode store provider must name a registered keyed
/// implementation, and the response-summary cap must be positive. Validating the provider here turns a
/// misconfigured key into a fail-loud startup error rather than a silent per-turn crash on the
/// fire-and-forget capture path (where the exception would be swallowed).
/// </summary>
public sealed class WorkMemoryConfigValidator : AbstractValidator<WorkMemoryConfig>
{
    private static readonly string[] KnownStoreProviders = ["graph", "in_memory"];

    /// <summary>Initializes a new instance of the <see cref="WorkMemoryConfigValidator"/> class.</summary>
    public WorkMemoryConfigValidator()
    {
        RuleFor(x => x.StoreProvider)
            .NotEmpty().WithMessage("StoreProvider must be configured ('graph' or 'in_memory').")
            .Must(p => KnownStoreProviders.Contains(p))
            .WithMessage("StoreProvider must be one of: 'graph', 'in_memory'.");

        RuleFor(x => x.ResponseSummaryMaxChars)
            .GreaterThan(0).WithMessage("ResponseSummaryMaxChars must be > 0.");
    }
}
