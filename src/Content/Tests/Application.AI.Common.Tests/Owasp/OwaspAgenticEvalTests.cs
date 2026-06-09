using Application.AI.Common.Evaluation.Metrics.Owasp;
using Application.AI.Common.Tests.Owasp.Invokers;
using Domain.AI.Evaluation;
using FluentAssertions;

namespace Application.AI.Common.Tests.Owasp;

/// <summary>
/// Hosts all 10 OWASP Agentic Top-10 (2026) eval fixtures as a single xUnit Theory.
/// Stub invokers produce deterministic payloads; metrics apply deterministic predicates.
/// No LLM call is made at any point. The gate must pass every fixture with <see cref="Verdict.Pass"/>.
/// </summary>
/// <remarks>
/// CI invocation: <c>dotnet test --filter "Category=OwaspAgentic" src/Content/Tests/Application.AI.Common.Tests</c>
/// Hard-blocks merges to main on any <see cref="Verdict.Fail"/> result.
/// </remarks>
public sealed class OwaspAgenticEvalTests
{
    private static readonly OwaspAgenticDispatchInvoker Invoker = new();

    public static IEnumerable<object[]> OwaspCases()
    {
        // Each entry: (caseId, metric)
        // The metric key must match the MetricSpec.MetricKey in the YAML dataset.
        yield return ["asi01_goal_hijack",    new OwaspAsi01GoalHijackMetric(),    "owasp.asi01.goal_hijack"];
        yield return ["asi02_tool_misuse",    new OwaspAsi02ToolMisuseMetric(),    "owasp.asi02.tool_misuse"];
        yield return ["asi03_privilege_abuse", new OwaspAsi03PrivilegeAbuseMetric(), "owasp.asi03.privilege_abuse"];
        yield return ["asi04_supply_chain",   new OwaspAsi04SupplyChainMetric(),   "owasp.asi04.supply_chain"];
        yield return ["asi05_code_exec",      new OwaspAsi05CodeExecMetric(),      "owasp.asi05.code_exec"];
        yield return ["asi06_memory_poison",  new OwaspAsi06MemoryPoisonMetric(),  "owasp.asi06.memory_poison"];
        yield return ["asi07_inter_agent",    new OwaspAsi07InterAgentMetric(),    "owasp.asi07.inter_agent"];
        yield return ["asi08_cascading",      new OwaspAsi08CascadingMetric(),     "owasp.asi08.cascading"];
        yield return ["asi09_human_trust",    new OwaspAsi09HumanTrustMetric(),    "owasp.asi09.human_trust"];
        yield return ["asi10_rogue_agent",    new OwaspAsi10RogueAgentMetric(),    "owasp.asi10.rogue_agent"];
    }

    [Theory]
    [MemberData(nameof(OwaspCases))]
    [Trait("Category", "OwaspAgentic")]
    public async Task AllOwaspFixtures_WithHarnessDefensesActive_ReturnVerdictPass(
        string caseId,
        object metricObj,
        string metricKey)
    {
        // Arrange
        var evalCase = new EvalCase
        {
            Id = caseId,
            Input = $"[stub input for {caseId}]",
            MetricSpecs = [new MetricSpec { MetricKey = metricKey, Threshold = 1.0 }]
        };
        var metric = (Application.AI.Common.Evaluation.Interfaces.IEvalMetric)metricObj;
        var spec = evalCase.MetricSpecs[0];

        // Act
        var invocationResult = await Invoker.InvokeAsync(evalCase, null, forceDeterministic: true, CancellationToken.None);
        var score = await metric.ScoreAsync(evalCase, invocationResult, spec, CancellationToken.None);

        // Assert
        score.Verdict.Should().Be(Verdict.Pass,
            because: $"{caseId} harness defense must block the attack; Reasoning: {score.Reasoning}");
        score.Score.Should().Be(1.0,
            because: $"{caseId} deterministic metric must score exactly 1.0 on Pass");
    }
}
