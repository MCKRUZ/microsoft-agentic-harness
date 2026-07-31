using Domain.Common.Config;
using Domain.Common.Config.AI;
using FluentValidation;
using Microsoft.Extensions.Options;

namespace Application.Core.CQRS.Evaluation.RunEvalSuite;

/// <summary>
/// Validates <see cref="RunEvalSuiteCommand"/> before execution. Catches obvious
/// misconfigurations (empty path list, invalid repeats/parallelism) before any work begins.
/// </summary>
/// <remarks>
/// <para>
/// The ceilings come from <c>AppConfig:AI:Evaluation</c> rather than being hard-coded. Until now
/// <c>EvalRunOptions.Parallelism</c> had a lower bound and no upper one here: the 1–128 range existed
/// only in the EvalRunner CLI's argument parsing, so any dispatcher that was not that CLI could ask
/// for unbounded concurrency against the model provider. Repeats and parallelism both multiply real
/// spend, so their ceilings belong somewhere an operator can set them.
/// </para>
/// <para>
/// Read through <see cref="IOptionsMonitor{TOptions}"/> per validation so tightening a ceiling takes
/// effect without a restart — the same reason <c>SearchDocumentsQueryValidator</c> does.
/// </para>
/// </remarks>
public sealed class RunEvalSuiteCommandValidator : AbstractValidator<RunEvalSuiteCommand>
{
    /// <summary>Initializes the validator with rules for command shape and configured cost ceilings.</summary>
    /// <param name="appConfigMonitor">Live application configuration supplying the evaluation ceilings.</param>
    public RunEvalSuiteCommandValidator(IOptionsMonitor<AppConfig> appConfigMonitor)
    {
        ArgumentNullException.ThrowIfNull(appConfigMonitor);

        RuleFor(x => x.DatasetPaths)
            .NotEmpty().WithMessage("At least one dataset path is required.");

        RuleFor(x => x.DatasetPaths)
            .Must(paths => paths is null || paths.Count <= Evaluation(appConfigMonitor).MaxDatasetsPerRun)
            .WithMessage(_ =>
                $"At most {Evaluation(appConfigMonitor).MaxDatasetsPerRun} datasets may be evaluated in one run.");

        RuleForEach(x => x.DatasetPaths)
            .NotEmpty().WithMessage("Dataset paths must not be empty strings.");

        RuleFor(x => x.Options.Repeats)
            .Must(repeats => repeats >= 1 && repeats <= Evaluation(appConfigMonitor).MaxRepeats)
            .WithMessage(_ => $"Repeats must be between 1 and {Evaluation(appConfigMonitor).MaxRepeats}.");

        RuleFor(x => x.Options.Parallelism)
            .Must(parallelism => parallelism >= 1 && parallelism <= Evaluation(appConfigMonitor).MaxParallelism)
            .WithMessage(_ => $"Parallelism must be between 1 and {Evaluation(appConfigMonitor).MaxParallelism}.");

        RuleFor(x => x.Options.FailRateThreshold)
            .InclusiveBetween(0.0, 1.0)
            .WithMessage("FailRateThreshold must be between 0.0 and 1.0.");
    }

    private static EvaluationConfig Evaluation(IOptionsMonitor<AppConfig> monitor) =>
        monitor.CurrentValue.AI.Evaluation;
}
