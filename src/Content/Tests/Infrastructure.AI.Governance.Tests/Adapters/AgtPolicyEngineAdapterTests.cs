using AgentGovernance.Policy;
using Domain.AI.Governance;
using Infrastructure.AI.Governance.Adapters;
using Tests.Common;
using Xunit;

namespace Infrastructure.AI.Governance.Tests.Adapters;

public sealed class AgtPolicyEngineAdapterTests : IDisposable
{
    private readonly PolicyEngine _engine = new();
    private readonly AgtPolicyEngineAdapter _adapter;
    private readonly List<string> _tempPaths = [];

    public AgtPolicyEngineAdapterTests()
    {
        _adapter = new AgtPolicyEngineAdapter(_engine);
    }

    public void Dispose()
    {
        foreach (var path in _tempPaths)
            File.Delete(path);
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
    public void EvaluateToolCall_WithArguments_PassesThemAsContext()
    {
        var args = new Dictionary<string, object?> { ["path"] = "/etc/passwd" };

        var decision = _adapter.EvaluateToolCall("agent-1", "read_file", args);

        Assert.True(decision.IsAllowed);
    }

    // #384: the shipped default-policy.yaml declares `default_action: allow` (snake_case, as
    // Microsoft.AgentGovernance's YAML deserializer requires) so a tool that matches none of its
    // rules must be allowed, not denied.
    [Fact]
    public void LoadPolicyFile_ShippedDefaultPolicy_UnmatchedToolIsAllowed()
    {
        var path = RepoRoot.Combine(
            "src", "Content", "Infrastructure", "Infrastructure.AI.Governance", "Policies", "default-policy.yaml");

        _adapter.LoadPolicyFile(path);
        var decision = _adapter.EvaluateToolCall("agent-1", "some_tool_no_rule_names");

        Assert.True(decision.IsAllowed);
        Assert.Equal(GovernancePolicyAction.Allow, decision.Action);
    }

    // AgentGovernance's Policy.FromYaml deserializes with UnderscoredNamingConvention and no
    // per-field override for DefaultAction (unlike ApiVersion, which has an explicit alias), and
    // IgnoreUnmatchedProperties() means a camelCase `defaultAction` key is silently dropped rather
    // than erroring — Policy.DefaultAction then silently falls back to Deny regardless of the
    // author's intent. LoadPolicyFile must fail loudly instead of accepting a policy document that
    // would misbehave silently.
    [Theory]
    [InlineData("defaultAction")]
    [InlineData("DefaultAction")]
    [InlineData("Default_Action")]
    [InlineData("default-action")]
    [InlineData("default action")]
    public void LoadPolicyFile_MisCasedDefaultAction_ThrowsActionableException(string misCasedKey)
    {
        var path = WriteTempPolicy($"""
            name: casing-mistake
            {misCasedKey}: allow
            rules:
              - name: block-exec
                condition: "tool == 'execute_command'"
                action: deny
            """);

        var ex = Assert.Throws<InvalidOperationException>(() => _adapter.LoadPolicyFile(path));

        Assert.Contains(misCasedKey, ex.Message);
        Assert.Contains("default_action", ex.Message);
    }

    // Premise control: the guard's whole justification is that AGT itself silently drops a mis-cased
    // key rather than erroring. This proves that premise directly against the real engine, bypassing
    // the guard entirely (LoadYaml, not LoadPolicyFile) — if a future AGT/YamlDotNet upgrade turns on
    // case-insensitive property matching, this is the test that should fail, not a puzzled read of why
    // the guard started rejecting valid policies.
    [Fact]
    public void EngineDirectly_MisCasedDefaultAction_IsSilentlyDroppedAndDeniesUnmatchedTool()
    {
        _engine.LoadYaml("""
            name: premise-control
            DefaultAction: allow
            rules:
              - name: block-exec
                condition: "tool == 'execute_command'"
                action: deny
            """);

        var decision = _adapter.EvaluateToolCall("agent-1", "read_file");

        Assert.False(decision.IsAllowed);
    }

    // The guard parses real YAML structure (the document's actual key set) rather than text-scanning
    // for a substring, so a correctly-keyed policy whose description happens to mention the mistaken
    // key by name (e.g. documenting the migration from #384) must not be rejected.
    [Fact]
    public void LoadPolicyFile_CorrectKeyWithMisCasedNameMentionedInDescription_DoesNotThrow()
    {
        var path = WriteTempPolicy("""
            name: describes-the-old-mistake
            default_action: allow
            description: "the old, wrong key was defaultAction — see #384"
            rules:
              - name: block-exec
                condition: "tool == 'execute_command'"
                action: deny
            """);

        var ex = Record.Exception(() => _adapter.LoadPolicyFile(path));

        Assert.Null(ex);
    }

    [Fact]
    public void LoadPolicyFile_SnakeCaseDefaultActionAllow_UnmatchedToolIsAllowed()
    {
        var path = WriteTempPolicy("""
            name: correctly-cased
            default_action: allow
            rules:
              - name: block-exec
                condition: "tool == 'execute_command'"
                action: deny
            """);

        _adapter.LoadPolicyFile(path);
        var decision = _adapter.EvaluateToolCall("agent-1", "read_file");

        Assert.True(decision.IsAllowed);
        Assert.Equal(GovernancePolicyAction.Allow, decision.Action);
    }

    [Fact]
    public void LoadPolicyFile_NoDefaultActionSpecified_DeniesUnmatchedToolByDefault()
    {
        var path = WriteTempPolicy("""
            name: no-default-specified
            rules:
              - name: block-exec
                condition: "tool == 'execute_command'"
                action: deny
            """);

        _adapter.LoadPolicyFile(path);
        var decision = _adapter.EvaluateToolCall("agent-1", "read_file");

        Assert.False(decision.IsAllowed);
    }

    private string WriteTempPolicy(string yaml)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.yaml");
        File.WriteAllText(path, yaml);
        _tempPaths.Add(path);
        return path;
    }
}
