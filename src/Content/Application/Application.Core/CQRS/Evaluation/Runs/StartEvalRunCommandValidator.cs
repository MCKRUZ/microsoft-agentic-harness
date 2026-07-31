using Domain.Common.Config;
using Domain.Common.Config.AI;
using FluentValidation;
using Microsoft.Extensions.Options;

namespace Application.Core.CQRS.Evaluation.Runs;

/// <summary>
/// Validates <see cref="StartEvalRunCommand"/> against the host's configured evaluation ceilings,
/// before a run is admitted.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why these bounds are repeated here.</strong> <c>RunEvalSuiteCommandValidator</c> already
/// applies them — but it applies them when the run <em>executes</em>, which on this path is after the
/// caller has been told 202, has been given a job id, and has spent one of its concurrency slots. A
/// caller asking for a thousand repeats would get an acceptance and then have to poll to discover a
/// refusal. Checking at admission turns that into an immediate answer, and the duplication is the
/// point: the execution-time check stays, because it is what protects every <em>other</em> dispatcher
/// of that command.
/// </para>
/// <para>
/// Read through <see cref="IOptionsMonitor{TOptions}"/> per validation so tightening a ceiling takes
/// effect without a restart, matching <c>RunEvalSuiteCommandValidator</c>.
/// </para>
/// </remarks>
public sealed class StartEvalRunCommandValidator : AbstractValidator<StartEvalRunCommand>
{
    /// <summary>Initializes the validator with the configured evaluation ceilings.</summary>
    /// <param name="appConfigMonitor">Live application configuration supplying the ceilings.</param>
    public StartEvalRunCommandValidator(IOptionsMonitor<AppConfig> appConfigMonitor)
    {
        ArgumentNullException.ThrowIfNull(appConfigMonitor);

        RuleFor(x => x.DatasetNames)
            .NotEmpty().WithMessage("At least one dataset name is required.");

        RuleFor(x => x.DatasetNames)
            .Must(names => names is null || names.Count <= Evaluation(appConfigMonitor).MaxDatasetsPerRun)
            .WithMessage(_ =>
                $"At most {Evaluation(appConfigMonitor).MaxDatasetsPerRun} datasets may be evaluated in one run.");

        RuleForEach(x => x.DatasetNames)
            .NotEmpty().WithMessage("Dataset names must not be empty strings.");

        RuleFor(x => x.Options.Repeats)
            .Must(repeats => repeats >= 1 && repeats <= Evaluation(appConfigMonitor).MaxRepeats)
            .WithMessage(_ => $"Repeats must be between 1 and {Evaluation(appConfigMonitor).MaxRepeats}.");

        RuleFor(x => x.Options.Parallelism)
            .Must(parallelism => parallelism >= 1 && parallelism <= Evaluation(appConfigMonitor).MaxParallelism)
            .WithMessage(_ => $"Parallelism must be between 1 and {Evaluation(appConfigMonitor).MaxParallelism}.");

        RuleFor(x => x.Options.FailRateThreshold)
            .InclusiveBetween(0.0, 1.0)
            .WithMessage("FailRateThreshold must be between 0.0 and 1.0.");

        // Not a duplicate of the run substrate's own identity handling: this is the assertion that the
        // transport resolved an identity at all. An unowned run would be readable by nobody and, worse,
        // would be stamped with an owner the scope filters read as global.
        RuleFor(x => x.OwnerId)
            .NotEmpty().WithMessage("The run's owner could not be established.");
    }

    private static EvaluationConfig Evaluation(IOptionsMonitor<AppConfig> monitor) =>
        monitor.CurrentValue.AI.Evaluation;
}
