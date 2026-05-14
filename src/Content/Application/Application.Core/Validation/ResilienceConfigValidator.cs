using Domain.Common.Config.AI.Resilience;
using FluentValidation;

namespace Application.Core.Validation;

/// <summary>
/// Validates <see cref="ResilienceConfig"/> ensuring fallback chain is populated when enabled,
/// circuit breaker ratios are in range, and all numeric tuning values are positive.
/// </summary>
public sealed class ResilienceConfigValidator : AbstractValidator<ResilienceConfig>
{
    private static readonly string[] ValidBackoffTypes = ["Exponential", "Linear"];

    public ResilienceConfigValidator()
    {
        RuleFor(x => x.FallbackChain)
            .NotEmpty()
            .WithMessage("FallbackChain must contain at least one provider when resilience is enabled.")
            .When(x => x.Enabled);

        RuleForEach(x => x.FallbackChain)
            .ChildRules(entry =>
            {
                entry.RuleFor(p => p.DeploymentId)
                    .NotEmpty()
                    .WithMessage("Each FallbackChain entry must have a DeploymentId.");

                entry.RuleFor(p => p.ClientType)
                    .IsInEnum()
                    .WithMessage("Each FallbackChain entry must have a valid ClientType.");
            });

        RuleFor(x => x.CircuitBreaker.FailureRatio)
            .Must(v => v > 0 && v < 1)
            .WithMessage("CircuitBreaker.FailureRatio must be between 0 and 1 exclusive.");

        RuleFor(x => x.CircuitBreaker.SamplingDurationSeconds)
            .GreaterThan(0)
            .WithMessage("CircuitBreaker.SamplingDurationSeconds must be > 0.");

        RuleFor(x => x.CircuitBreaker.MinimumThroughput)
            .GreaterThan(0)
            .WithMessage("CircuitBreaker.MinimumThroughput must be > 0.");

        RuleFor(x => x.CircuitBreaker.BreakDurationSeconds)
            .GreaterThan(0)
            .WithMessage("CircuitBreaker.BreakDurationSeconds must be > 0.");

        RuleFor(x => x.Retry.MaxAttempts)
            .GreaterThanOrEqualTo(1)
            .WithMessage("Retry.MaxAttempts must be >= 1.");

        RuleFor(x => x.Retry.BaseDelaySeconds)
            .GreaterThanOrEqualTo(0)
            .WithMessage("Retry.BaseDelaySeconds must be >= 0.");

        RuleFor(x => x.Retry.BackoffType)
            .Must(v => ValidBackoffTypes.Contains(v, StringComparer.OrdinalIgnoreCase))
            .WithMessage($"Retry.BackoffType must be one of: {string.Join(", ", ValidBackoffTypes)}.");

        RuleFor(x => x.Timeout.PerAttemptSeconds)
            .GreaterThan(0)
            .WithMessage("Timeout.PerAttemptSeconds must be > 0.");

        RuleFor(x => x.DegradedMode.MaxQueueSize)
            .GreaterThan(0)
            .WithMessage("DegradedMode.MaxQueueSize must be > 0.");

        RuleFor(x => x.DegradedMode.RetryQueueTtlSeconds)
            .GreaterThan(0)
            .WithMessage("DegradedMode.RetryQueueTtlSeconds must be > 0.");
    }
}
