using FluentValidation;

namespace Application.AI.Common.CQRS.SkillTraining.TrainSkill;

/// <summary>
/// Validates <see cref="TrainSkillCommand"/>. Range checks on every config knob and
/// cross-field invariants the scheduler/gate rely on.
/// </summary>
public sealed class TrainSkillCommandValidator : AbstractValidator<TrainSkillCommand>
{
    /// <summary>Allowed scheduler keys (case-insensitive). Centralized so DI and validator agree.</summary>
    public static readonly IReadOnlySet<string> AllowedSchedulers =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "cosine", "linear", "constant" };

    /// <summary>Initializes the validator.</summary>
    public TrainSkillCommandValidator()
    {
        RuleFor(x => x.RunId).NotEmpty();
        RuleFor(x => x.SkillId).NotEmpty();
        RuleFor(x => x.InitialSkill).NotNull();
        RuleFor(x => x.Config).NotNull();
        RuleFor(x => x.Domain)
            .Matches("^[a-z0-9][a-z0-9_-]{0,63}$")
            .WithMessage("Domain must be lower-case letters, digits, '_' or '-', starting with a letter or digit, at most 64 characters.");

        When(x => x.Config is not null, () =>
        {
            RuleFor(x => x.Config.Epochs).GreaterThanOrEqualTo(1);
            RuleFor(x => x.Config.StepsPerEpoch).GreaterThanOrEqualTo(1);
            RuleFor(x => x.Config.LrStart).GreaterThanOrEqualTo(1);
            RuleFor(x => x.Config.LrMin).GreaterThanOrEqualTo(1);
            RuleFor(x => x.Config.TrainBatchSize).GreaterThanOrEqualTo(1);
            RuleFor(x => x.Config.ValBatchSize).GreaterThanOrEqualTo(1);
            RuleFor(x => x.Config.Patience).GreaterThanOrEqualTo(1);
            RuleFor(x => x.Config.MixedWeight).InclusiveBetween(0.0, 1.0);

            RuleFor(x => x.Config.LrScheduler)
                .Must(s => !string.IsNullOrWhiteSpace(s) && AllowedSchedulers.Contains(s))
                .WithMessage($"LrScheduler must be one of: {string.Join(", ", AllowedSchedulers)}");

            RuleFor(x => x.Config)
                .Must(c => c.LrMin <= c.LrStart)
                .WithMessage("LrMin must not exceed LrStart.");
        });
    }
}
