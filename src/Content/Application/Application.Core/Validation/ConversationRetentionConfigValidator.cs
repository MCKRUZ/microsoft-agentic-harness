using Application.AI.Common.Services.Compliance;
using Domain.Common.Config.AI.Conversations;
using FluentValidation;

namespace Application.Core.Validation;

/// <summary>
/// Validates <see cref="ConversationRetentionConfig"/> at startup (#901). The sweep deletes conversations, so a period
/// that is too short is refused before it can run, not discovered afterwards.
/// </summary>
/// <remarks>
/// The sweep repeats the idle-period floor itself (<see cref="ConversationExpiry.MinimumIdlePeriod"/>), because a value
/// reloaded while the host runs is not re-validated here.
/// </remarks>
public sealed class ConversationRetentionConfigValidator : AbstractValidator<ConversationRetentionConfig>
{
    /// <summary>Initializes a new instance of the <see cref="ConversationRetentionConfigValidator"/> class.</summary>
    public ConversationRetentionConfigValidator()
    {
        RuleFor(x => x.IdlePeriod)
            .GreaterThanOrEqualTo(ConversationExpiry.MinimumIdlePeriod)
            .WithMessage("IdlePeriod must be at least one day; a shorter period would delete conversations still in use.");

        RuleFor(x => x.SweepInterval)
            .GreaterThan(TimeSpan.Zero)
            .WithMessage("SweepInterval must be positive.");

        RuleFor(x => x.BatchSize)
            .InclusiveBetween(1, 10_000)
            .WithMessage("BatchSize must be between 1 and 10000.");
    }
}
