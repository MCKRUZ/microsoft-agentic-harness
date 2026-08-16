using System.Text.RegularExpressions;
using Application.AI.Common.Interfaces.Tools;
using Domain.AI.Sandbox;
using FluentAssertions;
using Infrastructure.AI.Tools.Iac;
using Infrastructure.AI.Tools.Workspace;
using Infrastructure.AI.Tools;
using Microsoft.Extensions.DependencyInjection;
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
            Mock.Of<IServiceScopeFactory>()).RequiredCapabilities,
        [FileSystemTool.ToolName] = new FileSystemTool(
            Mock.Of<IFileSystemService>()).RequiredCapabilities,
        [WorkspaceReadFileTool.ToolName] = new WorkspaceReadFileTool(
            Mock.Of<Application.AI.Common.Interfaces.Workspace.IWorkspaceContextAccessor>()).RequiredCapabilities,
        [WorkspaceListFilesTool.ToolName] = new WorkspaceListFilesTool(
            Mock.Of<Application.AI.Common.Interfaces.Workspace.IWorkspaceContextAccessor>()).RequiredCapabilities,
        [DocumentSearchTool.ToolName] = new DocumentSearchTool(
            Mock.Of<Application.AI.Common.Interfaces.RAG.IRagOrchestrator>()).RequiredCapabilities,
    };

    private static readonly Regex ToolNamePattern = new(@"tool\s*==\s*'([a-zA-Z0-9_]+)'", RegexOptions.Compiled);

    private static readonly Regex RuleBlockPattern = new(
        @"- name:\s*(?<name>\S+)\s*\r?\n\s*condition:\s*""(?<condition>[^""]*)""",
        RegexOptions.Compiled);

    /// <summary>
    /// Parses the shipped YAML's three named rules into (rule name, tool names) pairs. Deliberately a
    /// small targeted regex, not a general YAML parser — the shipped file's shape is simple and
    /// stable, and a full parser would be more machinery than this test needs.
    /// </summary>
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

    [Fact]
    public void SandboxedExecutionTier_EveryNamedTool_DeclaresSubprocess()
    {
        var rules = ParseRuleToolNames();
        rules.Should().ContainKey("warn-sandboxed-execution");

        foreach (var toolName in rules["warn-sandboxed-execution"])
        {
            RealDeclarations.Should().ContainKey(toolName,
                $"'{toolName}' is named in warn-sandboxed-execution but this test does not yet resolve its RequiredCapabilities");
            RealDeclarations[toolName].Should().HaveFlag(ToolCapability.Subprocess,
                $"'{toolName}' is classified as sandboxed-execution but does not declare Subprocess");
        }
    }

    [Fact]
    public void FileMutationTier_EveryNamedTool_DeclaresFileWriteAndNotSubprocess()
    {
        var rules = ParseRuleToolNames();
        rules.Should().ContainKey("warn-file-mutation");

        foreach (var toolName in rules["warn-file-mutation"])
        {
            RealDeclarations.Should().ContainKey(toolName,
                $"'{toolName}' is named in warn-file-mutation but this test does not yet resolve its RequiredCapabilities");
            RealDeclarations[toolName].Should().HaveFlag(ToolCapability.FileWrite,
                $"'{toolName}' is classified as file-mutation but does not declare FileWrite");
            RealDeclarations[toolName].Should().NotHaveFlag(ToolCapability.Subprocess,
                $"'{toolName}' declares Subprocess and belongs in warn-sandboxed-execution, not warn-file-mutation");
        }
    }

    [Fact]
    public void ReadOnlyTier_EveryNamedTool_DeclaresNeitherSubprocessNorFileWrite()
    {
        var rules = ParseRuleToolNames();
        rules.Should().ContainKey("allow-read-only-tools");

        foreach (var toolName in rules["allow-read-only-tools"])
        {
            RealDeclarations.Should().ContainKey(toolName,
                $"'{toolName}' is named in allow-read-only-tools but this test does not yet resolve its RequiredCapabilities");
            RealDeclarations[toolName].Should().NotHaveFlag(ToolCapability.Subprocess,
                $"'{toolName}' declares Subprocess and does not belong in the read-only tier");
            RealDeclarations[toolName].Should().NotHaveFlag(ToolCapability.FileWrite,
                $"'{toolName}' declares FileWrite and does not belong in the read-only tier");
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
        var rules = ParseRuleToolNames();
        var allNamedTools = rules.Values.SelectMany(t => t).Distinct().ToList();

        allNamedTools.Should().NotBeEmpty("the parser must find at least the three shipped rules");
        allNamedTools.Should().OnlyContain(t => RealDeclarations.ContainsKey(t),
            "every tool named in default-policy.yaml must have a resolved RealDeclarations entry above");
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
