using AgentGovernance.Security;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.OpenTelemetry.Metrics;
using Domain.AI.Governance;

namespace Infrastructure.AI.Governance.Adapters;

/// <summary>Wraps the AGT <see cref="PromptInjectionDetector"/> behind the harness-owned <see cref="IPromptInjectionScanner"/>.</summary>
internal sealed class AgtPromptInjectionAdapter : IPromptInjectionScanner
{
    private readonly PromptInjectionDetector _detector;

    public AgtPromptInjectionAdapter(PromptInjectionDetector detector) => _detector = detector;

    public InjectionScanResult Scan(string input)
    {
        var result = _detector.Detect(input);

        if (!result.IsInjection)
            return InjectionScanResult.Clean();

        GovernanceMetrics.InjectionDetections.Add(1);

        return new InjectionScanResult(
            true,
            (Domain.AI.Governance.InjectionType)(int)result.InjectionType,
            (Domain.Common.Config.AI.ThreatLevel)(int)result.ThreatLevel,
            result.Confidence,
            result.MatchedPatterns?.AsReadOnly(),
            result.Explanation);
    }
}
