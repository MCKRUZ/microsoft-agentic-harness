using System.Diagnostics;
using System.Globalization;
using Application.AI.Common.Evaluation.Interfaces;
using Application.AI.Common.Evaluation.Models;
using Application.AI.Common.Exceptions;
using Application.Core.CQRS.Agents.ExecuteAgentTurn;
using Domain.AI.Evaluation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Evaluation.Invokers;

/// <summary>
/// Production <see cref="IAgentInvoker"/> that dispatches an
/// <see cref="ExecuteAgentTurnCommand"/> through MediatR so eval invocations
/// run the same pipeline (validation, content safety, governance, audit)
/// as a real agent turn.
/// </summary>
/// <remarks>
/// <para>
/// Override resolution: case-level <see cref="EvalCase.InvocationOverrides"/> win over
/// the run-level dictionary. Supported keys: <c>agent_name</c>, <c>system_prompt</c>,
/// <c>deployment</c>, <c>temperature</c>.
/// </para>
/// <para>
/// <c>forceDeterministic = true</c> always wins over any temperature override and
/// pins the turn to <c>0.0f</c>. Used by trace-replay flows.
/// </para>
/// </remarks>
public sealed class HarnessAgentInvoker : IAgentInvoker
{
    private const string AgentNameKey = "agent_name";
    private const string SystemPromptKey = "system_prompt";
    private const string DeploymentKey = "deployment";
    private const string TemperatureKey = "temperature";

    /// <inheritdoc />
    public IReadOnlySet<string> RecognizedOverrideKeys { get; } =
        new HashSet<string> { AgentNameKey, SystemPromptKey, DeploymentKey, TemperatureKey };

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<HarnessAgentInvoker> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="HarnessAgentInvoker"/> class.
    /// </summary>
    /// <param name="scopeFactory">Scope factory used to resolve <see cref="IMediator"/> per invocation.
    /// The invoker is a SINGLETON, but an agent-turn dispatch constructs pipeline behaviors that
    /// ctor-inject the SCOPED <c>IAgentExecutionContext</c>, so each invocation runs inside a
    /// fresh scope rather than against a root-bound mediator — which also gives every eval case
    /// its own execution context instead of sharing one across the whole run.</param>
    /// <param name="logger">Logger for invocation diagnostics.</param>
    public HarnessAgentInvoker(IServiceScopeFactory scopeFactory, ILogger<HarnessAgentInvoker> logger)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(logger);

        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    /// <exception cref="AgentInvocationFaultException">
    /// The harness could not run the turn: no <c>agent_name</c>, a configuration or internal failure, an unavailable
    /// screening service, or any other exception from the pipeline. A screening refusal is not a fault; it comes back
    /// as a failed result for metrics to score.
    /// </exception>
    public async Task<AgentInvocationResult> InvokeAsync(
        EvalCase @case,
        IReadOnlyDictionary<string, string>? runLevelOverrides,
        bool forceDeterministic,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(@case);

        var sw = Stopwatch.StartNew();

        var agentName = Resolve(@case.InvocationOverrides, runLevelOverrides, AgentNameKey);
        if (string.IsNullOrWhiteSpace(agentName))
        {
            throw new AgentInvocationFaultException(
                "Missing required 'agent_name' invocation override; cannot dispatch agent turn.");
        }

        var command = new ExecuteAgentTurnCommand
        {
            AgentName = agentName!,
            UserMessage = @case.Input,
            SystemPromptOverride = Resolve(@case.InvocationOverrides, runLevelOverrides, SystemPromptKey),
            DeploymentOverride = Resolve(@case.InvocationOverrides, runLevelOverrides, DeploymentKey),
            Temperature = ResolveTemperature(@case.Id, @case.InvocationOverrides, runLevelOverrides, forceDeterministic)
        };

        AgentTurnResult turn;
        try
        {
            // Dispatch inside a fresh scope: the MediatR pipeline resolves scoped services
            // (IAgentExecutionContext et al.), which a singleton must never pull from the root.
            await using var scope = _scopeFactory.CreateAsyncScope();
            var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
            turn = await mediator.Send(command, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (IsRefusal(ex))
        {
            // The screen judged the input and refused it: the pipeline working as designed, and an outcome a case
            // may expect, so it is returned for the metrics to score.
            return Failed(ex.Message, sw);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Agent invocation failed for case {CaseId} on agent {AgentName}.",
                @case.Id, agentName);
            throw new AgentInvocationFaultException(ex.Message, ex);
        }

        sw.Stop();
        ThrowIfTheHarnessCouldNotRun(turn, cancellationToken);
        return ToResult(turn, sw);
    }

    // A refusal is a screen that judged the input. One that could not run judged nothing, so a case relying on it
    // was not evaluated: that is a fault, or a down safety service would score as a correct block.
    private static bool IsRefusal(Exception ex) =>
        ex is IScreeningRefusal and not ContentSafetyUnavailableException;

    // The handler reports these as a returned failure rather than a throw. Passing them on as an empty answer made
    // the eval runner score a turn that never ran as a wrong answer, with the reason in no report (#1070).
    private static void ThrowIfTheHarnessCouldNotRun(AgentTurnResult turn, CancellationToken cancellationToken)
    {
        if (turn.Success || turn.ErrorKind is AgentTurnErrorKind.None)
            return;

        if (turn.ErrorKind is AgentTurnErrorKind.Cancelled)
            cancellationToken.ThrowIfCancellationRequested();

        throw new AgentInvocationFaultException(ChooseError(turn)!);
    }

    private static AgentInvocationResult ToResult(AgentTurnResult turn, Stopwatch sw)
    {
        // Per AgentInvocationResult contract: Output must be empty when Success is false.
        // A handler may still return a non-empty Response on failure (e.g. content-safety
        // refusal text) — preserve that text in Error instead so it surfaces in reports
        // without contaminating metric scoring. If neither Error nor Response carries any
        // detail on a failure, surface a generic diagnostic rather than null so reports
        // never show a "failed case with no reason".
        return new AgentInvocationResult
        {
            Success = turn.Success,
            Output = turn.Success ? (turn.Response ?? string.Empty) : string.Empty,
            Error = ChooseError(turn),
            ToolsInvoked = turn.ToolsInvoked,
            ToolCalls = turn.ToolCalls,
            InputTokens = turn.InputTokens,
            OutputTokens = turn.OutputTokens,
            CostUsd = turn.CostUsd,
            Model = turn.Model,
            Duration = sw.Elapsed,
            Governance = turn.Governance
        };
    }

    private static string? ChooseError(Application.Core.CQRS.Agents.ExecuteAgentTurn.AgentTurnResult turn)
    {
        if (turn.Success) return turn.Error;
        if (!string.IsNullOrEmpty(turn.Error)) return turn.Error;
        if (!string.IsNullOrEmpty(turn.Response)) return turn.Response;
        return "Agent turn failed without diagnostic.";
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

    private static string? Resolve(
        IReadOnlyDictionary<string, string>? caseOverrides,
        IReadOnlyDictionary<string, string>? runOverrides,
        string key)
    {
        if (caseOverrides is not null && caseOverrides.TryGetValue(key, out var v1) && !string.IsNullOrWhiteSpace(v1))
            return v1;
        if (runOverrides is not null && runOverrides.TryGetValue(key, out var v2) && !string.IsNullOrWhiteSpace(v2))
            return v2;
        return null;
    }

    private float? ResolveTemperature(
        string caseId,
        IReadOnlyDictionary<string, string>? caseOverrides,
        IReadOnlyDictionary<string, string>? runOverrides,
        bool forceDeterministic)
    {
        if (forceDeterministic) return 0.0f;

        var raw = Resolve(caseOverrides, runOverrides, TemperatureKey);
        if (raw is null) return null;

        if (float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        _logger.LogWarning(
            "Unparseable temperature override '{Raw}' for case {CaseId}; falling back to provider default.",
            raw, caseId);
        return null;
    }
}
