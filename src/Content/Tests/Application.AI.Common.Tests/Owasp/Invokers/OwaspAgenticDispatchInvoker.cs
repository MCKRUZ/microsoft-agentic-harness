using Application.AI.Common.Evaluation.Interfaces;
using Application.AI.Common.Evaluation.Models;
using Domain.AI.Evaluation;

namespace Application.AI.Common.Tests.Owasp.Invokers;

/// <summary>
/// Routes each OWASP Agentic eval case to its dedicated stub invoker by <see cref="EvalCase.Id"/>.
/// Implements <see cref="IAgentInvoker"/> so a single dispatch invoker can be passed to the
/// inline eval loop in <c>OwaspAgenticEvalTests</c> without requiring the <c>IEvalRunner</c>
/// Infrastructure dependency.
/// </summary>
public sealed class OwaspAgenticDispatchInvoker : IAgentInvoker
{
    private static readonly IReadOnlyDictionary<string, IAgentInvoker> Invokers =
        new Dictionary<string, IAgentInvoker>(StringComparer.OrdinalIgnoreCase)
        {
            ["asi01_goal_hijack"]  = new OwaspAsi01StubInvoker(),
            ["asi02_tool_misuse"]  = new OwaspAsi02StubInvoker(),
            ["asi03_privilege_abuse"] = new OwaspAsi03StubInvoker(),
            ["asi04_supply_chain"] = new OwaspAsi04StubInvoker(),
            ["asi05_code_exec"]    = new OwaspAsi05StubInvoker(),
            ["asi06_memory_poison"] = new OwaspAsi06StubInvoker(),
            ["asi07_inter_agent"]  = new OwaspAsi07StubInvoker(),
            ["asi08_cascading"]    = new OwaspAsi08StubInvoker(),
            ["asi09_human_trust"]  = new OwaspAsi09StubInvoker(),
            ["asi10_rogue_agent"]  = new OwaspAsi10StubInvoker()
        };

    /// <inheritdoc />
    public Task<AgentInvocationResult> InvokeAsync(
        EvalCase @case,
        IReadOnlyDictionary<string, string>? runLevelOverrides,
        bool forceDeterministic,
        CancellationToken cancellationToken)
    {
        if (!Invokers.TryGetValue(@case.Id, out var invoker))
            throw new InvalidOperationException(
                $"No stub invoker registered for OWASP eval case '{@case.Id}'. " +
                "Add an entry to OwaspAgenticDispatchInvoker.Invokers.");

        return invoker.InvokeAsync(@case, runLevelOverrides, forceDeterministic, cancellationToken);
    }
}
