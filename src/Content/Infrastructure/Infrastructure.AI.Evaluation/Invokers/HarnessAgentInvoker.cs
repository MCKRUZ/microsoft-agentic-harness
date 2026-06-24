using System.Diagnostics;
using System.Globalization;
using Application.AI.Common.Evaluation.Interfaces;
using Application.AI.Common.Evaluation.Models;
using Application.Core.CQRS.Agents.ExecuteAgentTurn;
using Domain.AI.Evaluation;
using MediatR;
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

    private readonly IMediator _mediator;
    private readonly ILogger<HarnessAgentInvoker> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="HarnessAgentInvoker"/> class.
    /// </summary>
    /// <param name="mediator">MediatR dispatcher used to send the agent-turn command.</param>
    /// <param name="logger">Logger for invocation diagnostics.</param>
    public HarnessAgentInvoker(IMediator mediator, ILogger<HarnessAgentInvoker> logger)
    {
        ArgumentNullException.ThrowIfNull(mediator);
        ArgumentNullException.ThrowIfNull(logger);

        _mediator = mediator;
        _logger = logger;
    }

    /// <inheritdoc />
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
            return Failed("Missing required 'agent_name' invocation override; cannot dispatch agent turn.", sw);
        }

        var command = new ExecuteAgentTurnCommand
        {
            AgentName = agentName!,
            UserMessage = @case.Input,
            SystemPromptOverride = Resolve(@case.InvocationOverrides, runLevelOverrides, SystemPromptKey),
            DeploymentOverride = Resolve(@case.InvocationOverrides, runLevelOverrides, DeploymentKey),
            Temperature = ResolveTemperature(@case.Id, @case.InvocationOverrides, runLevelOverrides, forceDeterministic)
        };

        try
        {
            var turn = await _mediator.Send(command, cancellationToken).ConfigureAwait(false);
            sw.Stop();

            // Per AgentInvocationResult contract: Output must be empty when Success is false.
            // A handler may still return a non-empty Response on failure (e.g. content-safety
            // refusal text) — preserve that text in Error instead so it surfaces in reports
            // without contaminating metric scoring. If neither Error nor Response carries any
            // detail on a failure, surface a generic diagnostic rather than null so reports
            // never show a "failed case with no reason".
            var output = turn.Success ? (turn.Response ?? string.Empty) : string.Empty;
            var error = ChooseError(turn);

            return new AgentInvocationResult
            {
                Success = turn.Success,
                Output = output,
                Error = error,
                ToolsInvoked = turn.ToolsInvoked,
                InputTokens = turn.InputTokens,
                OutputTokens = turn.OutputTokens,
                CostUsd = turn.CostUsd,
                Model = turn.Model,
                Duration = sw.Elapsed,
                Governance = turn.Governance
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Agent invocation failed for case {CaseId} on agent {AgentName}.",
                @case.Id, agentName);
            return Failed(ex.Message, sw);
        }
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
