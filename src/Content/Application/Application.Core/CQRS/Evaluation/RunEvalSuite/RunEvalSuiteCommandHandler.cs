using Application.AI.Common.Evaluation.Interfaces;
using Domain.AI.Evaluation;
using Domain.Common;
using Domain.Common.Config;
using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Application.Core.CQRS.Evaluation.RunEvalSuite;

/// <summary>
/// Handles <see cref="RunEvalSuiteCommand"/> by dispatching each declared dataset path
/// to its extension-matched <see cref="IEvalDatasetLoader"/>, then feeding the resulting
/// datasets through <see cref="IEvalRunner"/> with the supplied options.
/// </summary>
/// <remarks>
/// Translates expected failure modes (missing file, no matching loader, malformed dataset)
/// into <see cref="Result{T}"/> failures rather than bubbling exceptions through MediatR.
/// </remarks>
public sealed class RunEvalSuiteCommandHandler : IRequestHandler<RunEvalSuiteCommand, Result<EvalRunReport>>
{
    /// <summary>Threshold above which the handler logs a cost warning. Locked at 10 per the eval framework plan.</summary>
    public const int RepeatsCostWarningThreshold = 10;

    private readonly IReadOnlyDictionary<string, IEvalDatasetLoader> _loadersByExtension;
    private readonly IEvalRunner _runner;
    private readonly IEvalDatasetPathGuard _pathGuard;
    private readonly IOptionsMonitor<AppConfig> _config;
    private readonly ILogger<RunEvalSuiteCommandHandler> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RunEvalSuiteCommandHandler"/> class.
    /// </summary>
    /// <param name="loaders">All registered dataset loaders. Each loader's reported extensions are indexed individually.</param>
    /// <param name="runner">The configured eval runner.</param>
    /// <param name="pathGuard">
    /// Decides which dataset paths may be read. Applied here rather than at each dispatcher because a
    /// check every caller must remember to perform is one that will eventually be skipped.
    /// </param>
    /// <param name="config">
    /// Live configuration supplying the per-run cost ceilings. Read at execution time rather than
    /// captured so tightening a ceiling takes effect without a restart.
    /// </param>
    /// <param name="logger">Logger for orchestration diagnostics.</param>
    public RunEvalSuiteCommandHandler(
        IEnumerable<IEvalDatasetLoader> loaders,
        IEvalRunner runner,
        IEvalDatasetPathGuard pathGuard,
        IOptionsMonitor<AppConfig> config,
        ILogger<RunEvalSuiteCommandHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(loaders);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(pathGuard);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);

        var index = new Dictionary<string, IEvalDatasetLoader>(StringComparer.OrdinalIgnoreCase);
        foreach (var loader in loaders)
        {
            foreach (var ext in loader.Extensions ?? [])
            {
                var key = ext.TrimStart('.').ToLowerInvariant();
                if (string.IsNullOrEmpty(key)) continue;

                if (index.TryGetValue(key, out var existing) && !ReferenceEquals(existing, loader))
                {
                    logger.LogWarning(
                        "Dataset-loader registration conflict on extension '{Extension}': {New} shadows {Existing}.",
                        key, loader.GetType().Name, existing.GetType().Name);
                }

                index[key] = loader;
            }
        }
        _loadersByExtension = index;
        _runner = runner;
        _pathGuard = pathGuard;
        _config = config;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result<EvalRunReport>> Handle(RunEvalSuiteCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Defensive guard: validator should catch empty paths, but template consumers may
        // forget to register RequestValidationBehavior (flagged as a common mistake in CLAUDE.md).
        if (request.DatasetPaths is null || request.DatasetPaths.Count == 0)
        {
            return Result<EvalRunReport>.ValidationFailure(["At least one dataset path is required."]);
        }

        // Collected during handling and attached to the returned EvalRunReport so every
        // dispatcher (CLI, dashboard, scheduled job, REST endpoint, MCP tool) surfaces the
        // same advisory text via the report contract — no reliance on each host's logger
        // filter routing Warning to a visible sink.
        var warnings = new List<string>();

        if (request.Options.Repeats > RepeatsCostWarningThreshold)
        {
            var warning =
                $"Eval run requested with Repeats={request.Options.Repeats} (> {RepeatsCostWarningThreshold}). " +
                "LLM-judge cost scales linearly with repeats; consider reducing unless variance demands more.";
            warnings.Add(warning);
            _logger.LogWarning("{Warning}", warning);
        }

        var evaluation = _config.CurrentValue.AI.Evaluation;
        var repeats = Math.Max(1, request.Options.Repeats);

        var datasets = new List<EvalDataset>(request.DatasetPaths.Count);

        // Accumulated as each dataset lands rather than summed at the end, so a run that is already over
        // budget stops loading instead of parsing the rest of the list first. Widened to long so the
        // multiplication by repeats cannot overflow into a negative that passes the check.
        long totalCases = 0;
        long totalExecutions = 0;

        foreach (var path in request.DatasetPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // The guard both canonicalises and decides. Reading the caller's path directly from here
            // on would defeat it: a path containing ".." compares against an allowed root perfectly
            // well while resolving somewhere else entirely, so what gets opened must be the resolved
            // path, never the requested one.
            var decision = _pathGuard.Resolve(path);
            if (!decision.IsAllowed)
            {
                _logger.LogWarning(
                    "Eval dataset refused. Requested {Path}. Reason reported to caller: {Reason}",
                    path,
                    decision.Reason);

                return Result<EvalRunReport>.NotFound(decision.Reason ?? "Dataset is not available.");
            }

            var resolvedPath = decision.CanonicalPath!;

            var extension = Path.GetExtension(resolvedPath).TrimStart('.').ToLowerInvariant();
            if (!_loadersByExtension.TryGetValue(extension, out var loader))
            {
                _logger.LogWarning("No dataset loader registered for extension {Extension} ({Path})", extension, path);
                return Result<EvalRunReport>.Fail(
                    $"No dataset loader registered for extension '{extension}' (file: {path}).");
            }

            // Size is checked before the file is opened. The execution ceiling below can only be applied
            // once cases exist, and producing cases means parsing — so without this, naming one enormous
            // file makes the parse itself the cost, paid in full before anything is refused.
            if (!TryMeasure(resolvedPath, out var actualBytes))
            {
                _logger.LogWarning("Eval dataset refused: {Path} could not be measured.", path);
                return Result<EvalRunReport>.Fail($"Dataset '{path}' could not be read.");
            }

            if (evaluation.MaxDatasetBytes > 0 && actualBytes > evaluation.MaxDatasetBytes)
            {
                _logger.LogWarning(
                    "Eval dataset refused: {Path} is {ActualBytes} bytes, above the configured "
                    + "MaxDatasetBytes of {MaxBytes}.",
                    path,
                    actualBytes,
                    evaluation.MaxDatasetBytes);

                return Result<EvalRunReport>.ValidationFailure(
                [
                    $"Dataset '{path}' is larger than the configured limit of "
                    + $"{evaluation.MaxDatasetBytes} bytes."
                ]);
            }

            try
            {
                var dataset = await loader.LoadAsync(resolvedPath, cancellationToken).ConfigureAwait(false);
                datasets.Add(dataset);

                totalCases += dataset.Cases?.Count ?? 0;
                totalExecutions = totalCases * repeats;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (FileNotFoundException ex)
            {
                _logger.LogWarning(ex, "Loader reported missing file for {Path}", path);
                return Result<EvalRunReport>.NotFound($"Dataset file not found: {path}");
            }
            catch (InvalidDataException ex)
            {
                _logger.LogWarning(ex, "Failed to parse dataset {Path}", path);
                return Result<EvalRunReport>.Fail($"Failed to parse dataset {path}: {ex.Message}");
            }

            // Cost ceiling, applied as soon as it is exceeded rather than after the whole list is
            // parsed. Every case is a governed agent turn plus its LLM-judge calls, multiplied by
            // Repeats — so this is the last point at which a run's spend can be refused rather than
            // incurred. Refuse rather than truncate: quietly evaluating a subset would report a pass
            // rate for a suite that never ran.
            if (totalExecutions > evaluation.MaxCaseExecutionsPerRun)
            {
                _logger.LogWarning(
                    "Eval run refused: {TotalCases} cases x {Repeats} repeats = {TotalExecutions} "
                    + "executions, above the configured ceiling of {MaxExecutions}.",
                    totalCases,
                    repeats,
                    totalExecutions,
                    evaluation.MaxCaseExecutionsPerRun);

                return Result<EvalRunReport>.ValidationFailure(
                [
                    $"This run would perform at least {totalExecutions} case executions "
                    + $"({totalCases} cases x {repeats} repeats), above the configured ceiling of "
                    + $"{evaluation.MaxCaseExecutionsPerRun}. Narrow the run with a tag filter, reduce "
                    + "Repeats, split the datasets, or raise "
                    + "AppConfig:AI:Evaluation:MaxCaseExecutionsPerRun."
                ]);
            }
        }

        var report = await _runner.RunAsync(datasets, request.Options, cancellationToken).ConfigureAwait(false);

        // Attach handler-collected warnings to the report so they survive the dispatch
        // boundary; runner-emitted reports may not carry their own Warnings list.
        if (warnings.Count > 0)
        {
            report = report with { Warnings = warnings };
        }

        return Result<EvalRunReport>.Success(report);
    }

    /// <summary>Measures the dataset at <paramref name="path"/> so its size can be checked before it is parsed.</summary>
    /// <remarks>
    /// Returns <see langword="false"/> when the file cannot be measured at all. Waving it through would
    /// be the wrong direction: the whole point of a size cap is to bound memory <em>before</em> the
    /// parse, so skipping the check exactly when the size is unknown skips it in the one case where it
    /// would have mattered.
    /// </remarks>
    private static bool TryMeasure(string path, out long actualBytes)
    {
        try
        {
            actualBytes = new FileInfo(path).Length;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            actualBytes = 0;
            return false;
        }
    }
}
