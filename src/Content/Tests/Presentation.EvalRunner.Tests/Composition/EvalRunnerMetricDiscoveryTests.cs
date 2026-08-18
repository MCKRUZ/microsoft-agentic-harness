using Application.AI.Common.Evaluation.Interfaces;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Presentation.EvalRunner.Tests.Composition;

/// <summary>
/// Pins that every registered <see cref="IEvalMetric"/> — the 10 OWASP Agentic Top-10 metrics
/// specifically — resolves through the plain, non-keyed <c>IEnumerable&lt;IEvalMetric&gt;</c>
/// enumeration (#436).
/// </summary>
/// <remarks>
/// <see cref="Infrastructure.AI.Evaluation.Runners.EvalRunner"/> builds its metric lookup table
/// exclusively from that non-keyed enumeration (<c>EvalRunner.cs</c>). Before #436, the 10 OWASP
/// metrics were registered keyed-only, so every case in
/// <c>eval-datasets/owasp-agentic-top-10.yaml</c> silently resolved to EvalRunner's
/// "No registered metric with key '...'" branch and scored a non-gating <c>Verdict.Warn</c>
/// instead of ever actually running. This test fails loudly the same way if that regresses.
/// </remarks>
public sealed class EvalRunnerMetricDiscoveryTests
{
    private static readonly string[] OwaspMetricKeys =
    [
        "owasp.asi01.goal_hijack", "owasp.asi02.tool_misuse", "owasp.asi03.privilege_abuse",
        "owasp.asi04.supply_chain", "owasp.asi05.code_exec", "owasp.asi06.memory_poison",
        "owasp.asi07.inter_agent", "owasp.asi08.cascading", "owasp.asi09.human_trust",
        "owasp.asi10.rogue_agent",
    ];

    [Fact]
    public void NonKeyed_IEvalMetric_enumeration_includes_every_owasp_metric()
    {
        var services = EvalRunnerTestComposition.BuildServices();
        using var provider = services.BuildServiceProvider();

        var resolvedKeys = provider.GetServices<IEvalMetric>().Select(m => m.Key).ToList();

        resolvedKeys.Should().Contain(OwaspMetricKeys,
            "the 10 OWASP metrics must be discoverable via the same non-keyed IEnumerable<IEvalMetric> " +
            "EvalRunner resolves its metric map from — a keyed-only registration is invisible to it " +
            "and silently no-ops every case that references the metric (#436)");
    }
}
