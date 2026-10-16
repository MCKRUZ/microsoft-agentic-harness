using System.Text.RegularExpressions;
using Domain.Common.Config.AI.SkillTraining;
using FluentValidation;

namespace Application.Core.Validation;

/// <summary>
/// Validates <see cref="SkillTrainingConfig"/>: a usable evidence root and agent name, a turn budget,
/// a rollout agent when a dataset is configured, and a work description that describes the work rather
/// than its measurement (#843).
/// </summary>
public sealed partial class SkillTrainingConfigValidator : AbstractValidator<SkillTrainingConfig>
{
    /// <summary>The longest work description accepted, in characters.</summary>
    public const int MaxWorkDescriptionLength = 2000;

    /// <summary>The most turns a proposer session may be configured for.</summary>
    public const int MaxProposerTurnsCeiling = 50;

    /// <summary>The most rollouts that may be configured to run at once.</summary>
    public const int MaxRolloutConcurrencyCeiling = 16;

    // Whole words only, so ordinary vocabulary ("scoreboard" aside) is not caught by accident. "Evaluation"
    // is left out on purpose: it is common in the work itself ("risk evaluation").
    [GeneratedRegex(@"\b(benchmark|grader|grading|rubric|score|scores|scoring)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MeasurementWords();

    [GeneratedRegex(@"^[a-z0-9][a-z0-9_-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex AgentNamePattern();

    /// <summary>Initializes the rules.</summary>
    public SkillTrainingConfigValidator()
    {
        RuleFor(x => x.EvidenceRoot)
            .NotEmpty().WithMessage("EvidenceRoot must be set; the proposer's evidence is written there.");

        RuleFor(x => x.ProposerAgentName)
            .NotEmpty().WithMessage("ProposerAgentName must be set.")
            .Must(name => AgentNamePattern().IsMatch(name ?? string.Empty))
            .WithMessage("ProposerAgentName must be lower-case letters, digits, '_' or '-', starting with a letter or digit.");

        RuleFor(x => x.MaxProposerTurns)
            .InclusiveBetween(1, MaxProposerTurnsCeiling)
            .WithMessage($"MaxProposerTurns must be between 1 and {MaxProposerTurnsCeiling}.");

        RuleFor(x => x.MaxRolloutConcurrency)
            .InclusiveBetween(1, MaxRolloutConcurrencyCeiling)
            .WithMessage($"MaxRolloutConcurrency must be between 1 and {MaxRolloutConcurrencyCeiling}.");

        RuleFor(x => x.RolloutAgentName)
            .Must(name => AgentNamePattern().IsMatch(name ?? string.Empty))
            .When(x => !string.IsNullOrWhiteSpace(x.DatasetPath))
            .WithMessage("RolloutAgentName must be set when DatasetPath is: lower-case letters, digits, '_' or '-', starting with a letter or digit.");

        RuleFor(x => x.WorkDescription)
            .MaximumLength(MaxWorkDescriptionLength)
            .WithMessage($"WorkDescription must be at most {MaxWorkDescriptionLength} characters.")
            .Must(text => string.IsNullOrEmpty(text) || !MeasurementWords().IsMatch(text))
            .WithMessage("WorkDescription must describe the work, not its measurement: remove words such as benchmark, grader, rubric or score.");
    }
}
