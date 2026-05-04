using Domain.AI.Governance;
using Xunit;

namespace Domain.AI.Tests.Governance;

public sealed class GovernanceDecisionTests
{
    [Fact]
    public void Allowed_FactoryMethod_CreatesAllowedDecision()
    {
        var decision = GovernanceDecision.Allowed(0.05);

        Assert.True(decision.IsAllowed);
        Assert.Equal(GovernancePolicyAction.Allow, decision.Action);
        Assert.Equal(0.05, decision.EvaluationMs);
    }

    [Fact]
    public void Denied_FactoryMethod_CreatesDeniedDecision()
    {
        var decision = GovernanceDecision.Denied("blocked_tools", "default-policy", "Tool is on the block list");

        Assert.False(decision.IsAllowed);
        Assert.Equal(GovernancePolicyAction.Deny, decision.Action);
        Assert.Equal("blocked_tools", decision.MatchedRule);
        Assert.Equal("default-policy", decision.PolicyName);
        Assert.Equal("Tool is on the block list", decision.Reason);
    }

    [Fact]
    public void InjectionScanResult_Clean_IsNotInjection()
    {
        var result = InjectionScanResult.Clean();

        Assert.False(result.IsInjection);
        Assert.Equal(ThreatLevel.None, result.ThreatLevel);
        Assert.Equal(InjectionType.None, result.InjectionType);
    }

    [Fact]
    public void InjectionScanResult_Detected_HasCorrectProperties()
    {
        var result = new InjectionScanResult(
            IsInjection: true,
            InjectionType: InjectionType.DirectOverride,
            ThreatLevel: ThreatLevel.High,
            Confidence: 0.95,
            MatchedPatterns: ["ignore previous instructions"],
            Explanation: "Direct override attempt detected");

        Assert.True(result.IsInjection);
        Assert.Equal(InjectionType.DirectOverride, result.InjectionType);
        Assert.Equal(ThreatLevel.High, result.ThreatLevel);
        Assert.Equal(0.95, result.Confidence);
    }

    [Fact]
    public void McpToolScanResult_NoThreats_IsSafe()
    {
        var result = McpToolScanResult.Safe("test-tool");

        Assert.True(result.IsSafe);
        Assert.Empty(result.Threats);
    }
}
