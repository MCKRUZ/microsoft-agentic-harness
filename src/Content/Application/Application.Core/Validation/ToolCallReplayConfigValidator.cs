using Application.AI.Common.Services;
using Domain.Common.Config.AI.Conversations;
using FluentValidation;

namespace Application.Core.Validation;

/// <summary>
/// Validates <see cref="ToolCallReplayConfig"/>. <see cref="ToolCallReplayConfig.MaxVerbatimChars"/>
/// must stay within <c>[0, ToolCallReplayTreatment.WithholdCeilingChars]</c> — the upper bound is a
/// security invariant, not a tuning knob: letting a deployment configure a verbatim ceiling above the
/// point where structural secret-redaction stops being trustworthy (#391) would let that deployment
/// silently opt into replaying unredactable content to the model. This is the startup-time half of
/// that guarantee; <see cref="ToolCallReplayTreatment"/> itself clamps and logs defensively too, in
/// case a config value changes at runtime without going back through validation.
/// </summary>
/// <remarks>
/// Auto-discovered via <c>AddValidatorsFromAssembly</c> on the Application.Core assembly — no manual
/// registration required.
/// </remarks>
public sealed class ToolCallReplayConfigValidator : AbstractValidator<ToolCallReplayConfig>
{
    /// <summary>Initializes a new instance of the <see cref="ToolCallReplayConfigValidator"/> class.</summary>
    public ToolCallReplayConfigValidator()
    {
        RuleFor(x => x.MaxVerbatimChars)
            .InclusiveBetween(0, ToolCallReplayTreatment.WithholdCeilingChars)
            .WithMessage(
                $"MaxVerbatimChars must be between 0 and {ToolCallReplayTreatment.WithholdCeilingChars} " +
                "(the size above which the structural secret-redaction pass falls back to a regex-only " +
                "scan and cannot be trusted to replay verbatim).");

        // Non-negative only, with no upper bound: unlike MaxVerbatimChars these are cost ceilings, not
        // security ones — every payload they count has already been sanitized, redacted and size-capped
        // individually. A deployment on a very large context window raising either one is a legitimate
        // trade it can make for itself; a negative value is just nonsense that would disable the bound.
        RuleFor(x => x.MaxCallsPerTurn)
            .GreaterThanOrEqualTo(0)
            .WithMessage("MaxCallsPerTurn must be zero or greater.");

        RuleFor(x => x.MaxReplayedChars)
            .GreaterThanOrEqualTo(0)
            .WithMessage("MaxReplayedChars must be zero or greater.");
    }
}
