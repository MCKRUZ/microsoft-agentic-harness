using System.Text.RegularExpressions;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.OpenTelemetry.Metrics;
using Domain.AI.Governance;
using Domain.Common.Config.AI;

namespace Infrastructure.AI.Governance.Adapters;

/// <summary>Pattern-based MCP tool security scanner. Standalone implementation — AGT does not include MCP scanning.</summary>
internal sealed partial class McpSecurityScannerAdapter : IMcpSecurityScanner
{
    public McpToolScanResult ScanTool(string toolName, string toolDescription, string? toolSchema = null)
    {
        GovernanceMetrics.McpScans.Add(1);
        var threats = new List<McpToolThreat>();

        ScanForToolPoisoning(toolDescription, threats);
        ScanForHiddenInstructions(toolDescription, toolSchema, threats);
        ScanForDescriptionInjection(toolDescription, threats);
        ScanForTyposquatting(toolName, threats);

        if (threats.Count > 0)
            GovernanceMetrics.McpThreats.Add(threats.Count);

        return threats.Count == 0
            ? McpToolScanResult.Safe(toolName)
            : new McpToolScanResult(toolName, false, threats.AsReadOnly());
    }

    public IReadOnlyList<McpToolScanResult> ScanTools(
        IEnumerable<(string Name, string Description, string? Schema)> tools) =>
        tools.Select(t => ScanTool(t.Name, t.Description, t.Schema)).ToList().AsReadOnly();

    private static void ScanForToolPoisoning(string description, List<McpToolThreat> threats)
    {
        if (ToolPoisoningPattern().IsMatch(description))
        {
            threats.Add(new McpToolThreat(
                McpThreatType.ToolPoisoning,
                ThreatLevel.High,
                "Tool description contains instruction-override language",
                0.85));
        }
    }

    private static void ScanForHiddenInstructions(string description, string? schema, List<McpToolThreat> threats)
    {
        var textToScan = schema is not null ? description + schema : description;

        if (ZeroWidthPattern().IsMatch(textToScan))
        {
            threats.Add(new McpToolThreat(
                McpThreatType.HiddenInstruction,
                ThreatLevel.Critical,
                "Content contains zero-width or invisible Unicode characters",
                0.95));
        }

        if (Base64BlockPattern().IsMatch(textToScan))
        {
            threats.Add(new McpToolThreat(
                McpThreatType.HiddenInstruction,
                ThreatLevel.Medium,
                "Content contains base64-encoded blocks that may hide instructions",
                0.6));
        }
    }

    private static void ScanForDescriptionInjection(string description, List<McpToolThreat> threats)
    {
        if (DescriptionInjectionPattern().IsMatch(description))
        {
            threats.Add(new McpToolThreat(
                McpThreatType.DescriptionInjection,
                ThreatLevel.High,
                "Tool description contains prompt injection patterns",
                0.8));
        }
    }

    private static void ScanForTyposquatting(string toolName, List<McpToolThreat> threats)
    {
        if (TyposquattingPattern().IsMatch(toolName))
        {
            threats.Add(new McpToolThreat(
                McpThreatType.Typosquatting,
                ThreatLevel.Medium,
                "Tool name contains characters commonly used in typosquatting",
                0.7));
        }
    }

    [GeneratedRegex(@"\b(ignore|override|disregard|forget)\b.{0,30}\b(previous|above|prior|system|instructions?|prompt)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex ToolPoisoningPattern();

    [GeneratedRegex(@"[​‌‍⁠﻿]")]
    private static partial Regex ZeroWidthPattern();

    [GeneratedRegex(@"[A-Za-z0-9+/]{40,}={0,2}")]
    private static partial Regex Base64BlockPattern();

    [GeneratedRegex(@"\b(you\s+(are|must|should|will)|act\s+as|pretend|role\s*play|system\s*prompt|<\s*/?system\s*>)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex DescriptionInjectionPattern();

    // Homoglyph characters commonly used in typosquatting: Cyrillic lookalikes, special Unicode
    [GeneratedRegex(@"[Ѐ-ӿԀ-ԯ‐-―！-～]")]
    private static partial Regex TyposquattingPattern();
}
