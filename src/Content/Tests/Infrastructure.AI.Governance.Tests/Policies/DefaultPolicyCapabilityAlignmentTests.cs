using System.Text.RegularExpressions;
using Application.AI.Common.Interfaces.Tools;
using Domain.AI.Sandbox;
using FluentAssertions;
using Infrastructure.AI.Tools.Iac;
using Infrastructure.AI.Tools.Workspace;
using Infrastructure.AI.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Tests.Common;
using Xunit;

namespace Infrastructure.AI.Governance.Tests.Policies;

/// <summary>
/// Guard test for the mechanism gap #387 closes: <c>default-policy.yaml</c> sorts tools into risk
/// tiers by hand, and nothing previously checked that classification against what the tools actually
/// declare via <see cref="Application.AI.Common.Interfaces.Tools.ITool.RequiredCapabilities"/> — the
/// same shape of drift #384 was, in a different field. This test parses the shipped YAML's three
/// rule conditions, resolves each named tool's real declaration, and fails if a tool's tier
/// contradicts what it actually needs. Extending the YAML with a new rule naming a tool this test
/// does not yet resolve is a silent gap in coverage, not a passing test — see
/// <see cref="AllNamedToolsAreCoveredByThisTest"/>, which catches exactly that.
/// </summary>
public sealed class DefaultPolicyCapabilityAlignmentTests
{
    private static readonly string PolicyPath = RepoRoot.Combine(
        "src", "Content", "Infrastructure", "Infrastructure.AI.Governance", "Policies", "default-policy.yaml");

    /// <summary>
    /// Resolves a tool's real <c>RequiredCapabilities</c>. Where a tool already exposes it as a public
    /// static const (shared with the sandbox runner it feeds — see <c>WorkspaceRunTestsTool</c>/
    /// <c>IacPlanTool</c>'s <c>RequiredSandboxCapabilities</c>), this reads that const directly rather
    /// than constructing an instance: fewer moving parts, and a future constructor signature change to
    /// one of those tools cannot break this test for a reason unrelated to what it checks. The
    /// remaining tools have no such const, so those are read off a real instance built with inert
    /// mocked dependencies — every implementation here is a trivial expression-bodied property that
    /// never touches an injected field, so the mocks are never invoked.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, ToolCapability> RealDeclarations = new Dictionary<string, ToolCapability>
    {
        [WorkspaceRunTestsTool.ToolName] = WorkspaceRunTestsTool.RequiredSandboxCapabilities,
        [WorkspaceRunLintTool.ToolName] = WorkspaceRunLintTool.RequiredSandboxCapabilities,
        [IacPlanTool.ToolName] = IacPlanTool.RequiredSandboxCapabilities,
        [IacScanTool.ToolName] = IacScanTool.RequiredSandboxCapabilities,
        // Default interface member — only reachable through the ITool reference, not the concrete type.
        [IacGenerateTool.ToolName] = ((ITool)new IacGenerateTool(
            Mock.Of<IServiceProvider>(),
            Mock.Of<IOptionsMonitor<Domain.Common.Config.AppConfig>>())).RequiredCapabilities,
        [WorkspaceWriteFileTool.ToolName] = new WorkspaceWriteFileTool(
            Mock.Of<Application.AI.Common.Interfaces.Workspace.IWorkspaceContextAccessor>(),
            Mock.Of<IServiceScopeFactory>(),
            Mock.Of<ILogger<WorkspaceWriteFileTool>>()).RequiredCapabilities,
        [FileSystemTool.ToolName] = new FileSystemTool(
            Mock.Of<IFileSystemService>()).RequiredCapabilities,
        [WorkspaceReadFileTool.ToolName] = new WorkspaceReadFileTool(
            Mock.Of<Application.AI.Common.Interfaces.Workspace.IWorkspaceContextAccessor>()).RequiredCapabilities,
        [WorkspaceListFilesTool.ToolName] = new WorkspaceListFilesTool(
            Mock.Of<Application.AI.Common.Interfaces.Workspace.IWorkspaceContextAccessor>()).RequiredCapabilities,
        [DocumentSearchTool.ToolName] = new DocumentSearchTool(
            Mock.Of<Application.AI.Common.Interfaces.RAG.IRagOrchestrator>(),
            Mock.Of<ILogger<DocumentSearchTool>>()).RequiredCapabilities,
    };

    private static readonly Regex ToolNamePattern = new(@"tool\s*==\s*'([a-zA-Z0-9_]+)'", RegexOptions.Compiled);

    /// <summary>
    /// Matches a rule block name and condition, tolerating zero or more full-line comments between
    /// them — the shipped YAML is heavily commented and a rule with an inline comment between its
    /// <c>name</c> and <c>condition</c> lines is a plausible future edit given the file's existing
    /// style; silently dropping that block from <see cref="RuleToolNames"/> would defeat this test's
    /// whole purpose for exactly the rules a reader is most likely to add a comment to.
    /// </summary>
    private static readonly Regex RuleBlockPattern = new(
        @"- name:\s*(?<name>\S+)\s*\r?\n(?:\s*#[^\r\n]*\r?\n)*\s*condition:\s*""(?<condition>[^""]*)""",
        RegexOptions.Compiled);

    /// <summary>
    /// The shipped YAML's three named rules, parsed once into (rule name, tool names) pairs.
    /// Deliberately a small targeted regex, not a general YAML parser — the shipped file's shape is
    /// simple and stable, and a full parser would be more machinery than this test needs. `static
    /// readonly` (not a method re-run per test) so the file is read and parsed once per test-class run,
    /// not once per `[Fact]`/`[Theory]` row.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> RuleToolNames = ParseRuleToolNames();

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> ParseRuleToolNames()
    {
        var yaml = File.ReadAllText(PolicyPath);
        var result = new Dictionary<string, IReadOnlyList<string>>();

        foreach (System.Text.RegularExpressions.Match block in RuleBlockPattern.Matches(yaml))
        {
            var ruleName = block.Groups["name"].Value;
            var condition = block.Groups["condition"].Value;
            var toolNames = ToolNamePattern.Matches(condition).Select(m => m.Groups[1].Value).ToList();
            result[ruleName] = toolNames;
        }

        return result;
    }

    [Theory]
    [InlineData("warn-sandboxed-execution", ToolCapability.Subprocess, ToolCapability.None)]
    [InlineData("warn-file-mutation", ToolCapability.FileWrite, ToolCapability.Subprocess)]
    [InlineData("allow-read-only-tools", ToolCapability.None,
        ToolCapability.Subprocess | ToolCapability.FileWrite | ToolCapability.NetworkAccess)]
    public void EveryNamedTool_DeclaresMustHaveFlagsAndNoneOfMustNotHaveFlags(
        string ruleName, ToolCapability mustHave, ToolCapability mustNotHave)
    {
        RuleToolNames.Should().ContainKey(ruleName);

        foreach (var toolName in RuleToolNames[ruleName])
        {
            RealDeclarations.Should().ContainKey(toolName,
                $"'{toolName}' is named in {ruleName} but this test does not yet resolve its RequiredCapabilities");

            if (mustHave != ToolCapability.None)
                RealDeclarations[toolName].Should().HaveFlag(mustHave,
                    $"'{toolName}' is classified as {ruleName} but does not declare {mustHave}");

            if (mustNotHave != ToolCapability.None)
                foreach (ToolCapability flag in Enum.GetValues<ToolCapability>())
                    if (flag != ToolCapability.None && mustNotHave.HasFlag(flag))
                        RealDeclarations[toolName].Should().NotHaveFlag(flag,
                            $"'{toolName}' declares {flag} and does not belong in {ruleName}");
        }
    }

    /// <summary>
    /// Every tool named anywhere in the shipped policy must be one this test resolves — otherwise
    /// extending the YAML with a new rule silently adds a tool the coverage above never checks,
    /// which is the same "looks covered but isn't" shape as the drift #387 exists to prevent.
    /// </summary>
    [Fact]
    public void AllNamedToolsAreCoveredByThisTest()
    {
        var allNamedTools = RuleToolNames.Values.SelectMany(t => t).Distinct().ToList();

        allNamedTools.Should().NotBeEmpty("the parser must find at least the three shipped rules");
        allNamedTools.Should().OnlyContain(t => RealDeclarations.ContainsKey(t),
            "every tool named in default-policy.yaml must have a resolved RealDeclarations entry above");
    }

    /// <summary>
    /// Regression guard for a code-review finding on this same test: a comment line between a rule's
    /// <c>name</c> and <c>condition</c> used to make <see cref="RuleBlockPattern"/> skip the whole
    /// block, silently dropping its tools from <see cref="RuleToolNames"/> and every check above.
    /// </summary>
    [Fact]
    public void RuleBlockPattern_CommentBetweenNameAndCondition_StillMatches()
    {
        const string yaml = """
            - name: warn-sandboxed-execution
              # a future maintainer's note explaining this rule
              condition: "tool == 'run_tests'"
              action: warn
            """;

        var match = RuleBlockPattern.Match(yaml);

        match.Success.Should().BeTrue("a comment between name and condition must not hide the rule block");
        match.Groups["name"].Value.Should().Be("warn-sandboxed-execution");
        match.Groups["condition"].Value.Should().Be("tool == 'run_tests'");
    }

    /// <summary>
    /// Proves the tier checks above actually detect drift rather than passing vacuously — asserts the
    /// check logic itself against a deliberately wrong tier assignment, without touching production
    /// tool declarations.
    /// </summary>
    [Fact]
    public void TierCheck_ToolMisclassifiedAsReadOnlyButDeclaresSubprocess_WouldFail()
    {
        var misclassified = new Dictionary<string, ToolCapability>
        {
            ["fake_tool"] = ToolCapability.FileRead | ToolCapability.Subprocess,
        };

        var act = () => misclassified["fake_tool"].Should().NotHaveFlag(ToolCapability.Subprocess,
            "a tool declaring Subprocess must never be accepted into the read-only tier");

        act.Should().Throw<Exception>();
    }
}
