using System.Diagnostics;
using Application.AI.Common.Evaluation.Interfaces;
using Application.AI.Common.Evaluation.Models;
using Domain.AI.Evaluation;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Evaluation.Invokers;

/// <summary>
/// An <see cref="IAgentInvoker"/> decorator that routes a case to a router probe instead of the
/// full agent turn when the case opts in via a <c>target: "router:&lt;key&gt;"</c> invocation
/// override. All other cases pass through unchanged to the wrapped <see cref="HarnessAgentInvoker"/>.
/// </summary>
/// <remarks>
/// <para>
/// This is the seam that lets the existing eval runner — which grades a single textual output per
/// case — also grade router decisions, without changing the runner, the domain model, or the
/// routers. For a router-target case the probe's primary label is packed into
/// <see cref="AgentInvocationResult.Output"/>, where the <c>routing_accuracy</c> metric reads it.
/// </para>
/// <para>
/// Router classifiers manage their own sampling temperature (economy-tier, classification-tuned), so
/// the runner's <c>forceDeterministic</c> flag — which pins the agent turn's temperature — does not
/// apply on the router path and is ignored there.
/// </para>
/// </remarks>
public sealed class RouterEvalInvoker : IAgentInvoker
{
    private const string TargetKey = "target";
    private const string RouterPrefix = "router:";

    private readonly HarnessAgentInvoker _inner;
    private readonly IReadOnlyDictionary<string, IRouterEvalProbe> _probesByKey;
    private readonly ILogger<RouterEvalInvoker> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RouterEvalInvoker"/> class.
    /// </summary>
    /// <param name="inner">The wrapped harness invoker handling non-router cases.</param>
    /// <param name="probes">All registered router probes; indexed by <see cref="IRouterEvalProbe.Key"/>.</param>
    /// <param name="logger">Logger for router-path diagnostics.</param>
    public RouterEvalInvoker(
        HarnessAgentInvoker inner,
        IEnumerable<IRouterEvalProbe> probes,
        ILogger<RouterEvalInvoker> logger)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(probes);
        ArgumentNullException.ThrowIfNull(logger);

        _inner = inner;
        _logger = logger;
        _probesByKey = probes.ToDictionary(p => p.Key, StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public async Task<AgentInvocationResult> InvokeAsync(
        EvalCase @case,
        IReadOnlyDictionary<string, string>? runLevelOverrides,
        bool forceDeterministic,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(@case);

        if (!TryResolveRouterKey(@case, out var routerKey))
        {
            return await _inner.InvokeAsync(@case, runLevelOverrides, forceDeterministic, cancellationToken)
                .ConfigureAwait(false);
        }

        var sw = Stopwatch.StartNew();

        if (!_probesByKey.TryGetValue(routerKey, out var probe))
        {
            var known = _probesByKey.Count == 0 ? "(none registered)" : string.Join(", ", _probesByKey.Keys.OrderBy(k => k));
            return Failed($"No router probe registered for key '{routerKey}'. Registered: {known}.", sw);
        }

        try
        {
            var decision = await probe.ClassifyAsync(@case.Input, @case.InvocationOverrides, cancellationToken)
                .ConfigureAwait(false);
            sw.Stop();

            return new AgentInvocationResult
            {
                Success = true,
                Output = decision.Label,
                Duration = sw.Elapsed
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Router probe '{RouterKey}' threw classifying case {CaseId}.", routerKey, @case.Id);
            return Failed(ex.Message, sw);
        }
    }

    /// <summary>
    /// Determines whether a case targets a router. A case routes to a probe only when it carries an
    /// explicit <c>target: "router:&lt;key&gt;"</c> override; any other value (absent, <c>"agent"</c>,
    /// or a non-router string) passes through to the agent. Requiring the prefix keeps the contract
    /// unambiguous: a typo'd or future non-router target is never silently hijacked onto the router
    /// path, and the agent invoker surfaces its own clear error if the override is malformed.
    /// </summary>
    private static bool TryResolveRouterKey(EvalCase @case, out string routerKey)
    {
        routerKey = string.Empty;
        if (!@case.InvocationOverrides.TryGetValue(TargetKey, out var target) || string.IsNullOrWhiteSpace(target))
        {
            return false;
        }

        target = target.Trim();
        if (!target.StartsWith(RouterPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        routerKey = target[RouterPrefix.Length..].Trim();
        return routerKey.Length > 0;
    }

    private static AgentInvocationResult Failed(string error, Stopwatch sw)
    {
        sw.Stop();
        return new AgentInvocationResult
        {
            Success = false,
            Output = string.Empty,
            Error = error,
            Duration = sw.Elapsed
        };
    }
}
