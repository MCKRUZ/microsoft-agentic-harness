using Application.AI.Common.Interfaces.Governance;
using Domain.AI.Governance;

namespace Infrastructure.AI.Governance.Adapters;

/// <summary>No-op policy engine used when governance is disabled.</summary>
internal sealed class NoOpPolicyEngine : IGovernancePolicyEngine
{
    public bool HasPolicies => false;
    public GovernanceDecision EvaluateToolCall(string agentId, string toolName, IReadOnlyDictionary<string, object?>? arguments = null) =>
        GovernanceDecision.Allowed();
    public void LoadPolicyFile(string yamlPath) { }
}

/// <summary>No-op injection scanner used when governance is disabled.</summary>
internal sealed class NoOpInjectionScanner : IPromptInjectionScanner
{
    public InjectionScanResult Scan(string input) => InjectionScanResult.Clean();
}

/// <summary>No-op MCP scanner used when governance is disabled.</summary>
internal sealed class NoOpMcpScanner : IMcpSecurityScanner
{
    public McpToolScanResult ScanTool(string toolName, string toolDescription, string? toolSchema = null) =>
        McpToolScanResult.Safe(toolName);
    public IReadOnlyList<McpToolScanResult> ScanTools(IEnumerable<(string Name, string Description, string? Schema)> tools) =>
        tools.Select(t => McpToolScanResult.Safe(t.Name)).ToList().AsReadOnly();
    public McpToolScanResult ScanContent(string sourceName, string content, bool includeLengthSensitiveRules) =>
        McpToolScanResult.Safe(sourceName);
}

/// <summary>No-op MCP tool-surface scanner used when governance is disabled.</summary>
internal sealed class NoOpMcpToolSurfaceScanner : IMcpToolSurfaceScanner
{
    public IReadOnlyList<McpSurfaceFinding> ScanSurface(IReadOnlyList<McpSurfaceTool> tools) => [];
    public void CommitDefinitionPins(IReadOnlyList<McpSurfaceTool> tools, IReadOnlySet<McpSurfaceToolReference> excludeFromCommit) { }
}

/// <summary>No-op response sanitizer used when governance is disabled.</summary>
internal sealed class NoOpResponseSanitizer : ICompositeResponseSanitizer
{
    public SanitizationResult Sanitize(string content, string? toolName = null) =>
        SanitizationResult.Clean(content ?? string.Empty);
}
