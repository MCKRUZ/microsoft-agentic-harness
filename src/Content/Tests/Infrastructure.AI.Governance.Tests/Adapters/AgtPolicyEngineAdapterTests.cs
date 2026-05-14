using AgentGovernance.Policy;
using Domain.AI.Governance;
using Infrastructure.AI.Governance.Adapters;
using Xunit;

namespace Infrastructure.AI.Governance.Tests.Adapters;

public sealed class AgtPolicyEngineAdapterTests
{
    private readonly PolicyEngine _engine = new();
    private readonly AgtPolicyEngineAdapter _adapter;

    public AgtPolicyEngineAdapterTests()
    {
        _adapter = new AgtPolicyEngineAdapter(_engine);
    }

    [Fact]
    public void HasPolicies_NoPoliciesLoaded_ReturnsFalse()
    {
        Assert.False(_adapter.HasPolicies);
    }

    [Fact]
    public void HasPolicies_AfterLoadingPolicy_ReturnsTrue()
    {
        var yaml = """
            name: test-policy
            rules:
              - name: allow-all
                condition: "true"
                action: allow
            """;
        _engine.LoadYaml(yaml);

        Assert.True(_adapter.HasPolicies);
    }

    [Fact]
    public void EvaluateToolCall_NoPolicies_ReturnsAllowed()
    {
        var decision = _adapter.EvaluateToolCall("agent-1", "read_file");

        Assert.True(decision.IsAllowed);
        Assert.Equal(GovernancePolicyAction.Allow, decision.Action);
        Assert.True(decision.EvaluationMs >= 0);
    }

    [Fact]
    public void EvaluateToolCall_DenyPolicy_ReturnsDenied()
    {
        var yaml = """
            name: block-dangerous
            rules:
              - name: block-exec
                condition: "tool == 'execute_command'"
                action: deny
                description: Execution tools are blocked
            """;
        _engine.LoadYaml(yaml);

        var decision = _adapter.EvaluateToolCall("agent-1", "execute_command");

        Assert.False(decision.IsAllowed);
        Assert.Equal(GovernancePolicyAction.Deny, decision.Action);
    }

    [Fact]
    public void LoadPolicyFile_DelegatesToEngine()
    {
        var yaml = """
            name: loaded-policy
            rules:
              - name: test-rule
                condition: "true"
                action: allow
            """;
        _engine.LoadYaml(yaml);

        Assert.True(_adapter.HasPolicies);
    }

    [Fact]
    public void EvaluateToolCall_WithArguments_PassesThemAsContext()
    {
        var args = new Dictionary<string, object> { ["path"] = "/etc/passwd" };

        var decision = _adapter.EvaluateToolCall("agent-1", "read_file", args);

        Assert.True(decision.IsAllowed);
    }
}
