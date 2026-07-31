using Application.AI.Common.Interfaces.Runs;
using Application.Core.CQRS.Evaluation.RunEvalSuite;
using Domain.AI.Runs;
using Domain.Common.Config;
using Infrastructure.AI.Evaluation;
using Infrastructure.AI.Runs;

namespace Presentation.ExecutionApi.Extensions;

/// <summary>
/// Opt-in wiring for the evaluation framework in this host.
/// </summary>
/// <remarks>
/// <para>
/// The framework is not registered by the shared composition root — a host that never evaluates should
/// not carry the YAML loader, the metric singletons, the reporters, and the harness agent invoker on
/// every cold start. Hosts that do evaluate call this after <c>GetServices</c>, so the real
/// <c>IEvalRunner</c> replaces the fail-fast <c>NotConfiguredEvalRunner</c> default by last-write-wins.
/// </para>
/// </remarks>
public static class ExecutionApiEvaluationExtensions
{
    /// <summary>
    /// Registers the evaluation framework when <c>AppConfig:AI:Evaluation:Enabled</c> is set, and
    /// refuses to start when it is set without any dataset root configured.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">Configuration to read the evaluation section from.</param>
    /// <returns>The service collection, for chaining.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when evaluation is enabled but <c>DatasetRoots</c> is empty. This is a deliberate
    /// startup failure rather than a warning: an empty root list means "read any path the process
    /// can reach", which is the correct default for the local CLI and never correct for a host that
    /// serves callers it does not trust. A host that booted anyway would look configured while
    /// honouring whatever path a caller sent.
    /// </exception>
    public static IServiceCollection AddExecutionApiEvaluation(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var evaluation = configuration
            .GetSection("AppConfig:AI:Evaluation")
            .Get<Domain.Common.Config.AI.EvaluationConfig>() ?? new Domain.Common.Config.AI.EvaluationConfig();

        if (!evaluation.Enabled)
            return services;

        var roots = evaluation.DatasetRoots
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .ToList();

        if (roots.Count == 0)
        {
            throw new InvalidOperationException(
                "AppConfig:AI:Evaluation:Enabled is true but DatasetRoots is empty. Evaluation reads "
                + "dataset files named by the caller, and with no roots configured that is unconfined. "
                + "Configure at least one directory under AppConfig:AI:Evaluation:DatasetRoots, or set "
                + "Enabled to false.");
        }

        services.AddEvaluationDependencies();

        // Record that confinement was verified HERE, at startup, while the roots above were proven
        // non-empty. The guard cannot derive this for itself: it is a lazy singleton, so its constructor
        // first runs on the first eval dispatch, and configuration is bound with reloadOnChange — a
        // reload that emptied DatasetRoots in between would have it conclude the host started
        // unconfined. Latching the verdict at composition time is what makes the ratchet real.
        services.AddSingleton(new EvalConfinementLatch(StartedConfined: true));

        // The executor, registered only inside this branch — the one piece that is genuinely
        // host-specific. A host that has not enabled evaluation has no executor for
        // RunKind.Evaluation at all, and the dispatcher fails a run whose kind has no executor. That is
        // the fail-closed order: the endpoints refuse first, and if anything ever queued an eval run
        // past them, the dispatcher still would not execute it.
        //
        // Keyed by RunKind and scoped, exactly as the workflow executor is: the dispatcher creates a
        // fresh scope per run and resolves the executor inside it.
        //
        // The submission store it reads is registered unconditionally with the rest of the run
        // substrate. It has to be: the eval CQRS handlers are discovered by assembly scanning, so they
        // exist in every host, and a conditionally-registered dependency would fail ValidateOnBuild
        // everywhere evaluation is off.
        services.AddKeyedScoped<IRunKindExecutor, EvalRunKindExecutor>(RunKind.Evaluation);

        return services;
    }
}
