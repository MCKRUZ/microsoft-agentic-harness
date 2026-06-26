using Domain.Common.Config.AI.WorkMemory;
using FluentValidation;

namespace Application.Core.Validation;

/// <summary>
/// Validates <see cref="WorkMemoryConfig"/>: the episode store provider must name a registered keyed
/// implementation, and the response-summary cap must be positive. Auto-discovered via
/// <c>AddValidatorsFromAssembly</c>, consistent with the sibling config validators
/// (<c>LearningsConfigValidator</c> et al.).
/// </summary>
/// <remarks>
/// The hard fail-loud guarantee for a misconfigured <see cref="WorkMemoryConfig.StoreProvider"/> is
/// enforced independently at DI registration time (<c>AddKnowledgeGraphDependencies</c>), which throws
/// at startup before the app serves a turn. This validator additionally covers the numeric range and
/// is available to any explicit config-validation pass.
/// </remarks>
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
