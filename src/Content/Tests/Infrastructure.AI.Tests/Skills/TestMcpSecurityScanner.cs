using Application.AI.Common.Interfaces.Governance;
using Domain.AI.Governance;
using Domain.Common.Config.AI;
using Microsoft.Extensions.Options;
using Moq;

namespace Infrastructure.AI.Tests;

/// <summary>
/// Builds <see cref="IMcpSecurityScanner"/> and <see cref="IOptionsMonitor{TOptions}"/> doubles for
/// tests whose subject is skill/agent manifest parsing rather than injection scanning — the scanner
/// itself is covered by <c>McpSecurityScannerAdapterTests</c> against the real implementation.
/// </summary>
/// <remarks>
/// <b>This double proves nothing about the scanner.</b> A test that means to assert a manifest is
/// refused must not use <see cref="AlwaysSafe"/> — it reports every scan as safe, so such a test
/// would pass while asserting nothing. Refusal-triggered parser behavior (throwing, the findings
/// carried, not leaking scanned text) is covered by <c>SkillMetadataParserManifestScanningTests</c> /
/// <c>AgentMetadataParserManifestScanningTests</c> against a configurable scanner double; the
/// scanner's own detection correctness is covered separately by
/// <c>Infrastructure.AI.Governance.Tests</c> against the real <c>McpSecurityScannerAdapter</c> —
/// <c>Infrastructure.AI.Tests</c> has no visibility into that internal type.
/// </remarks>
internal static class TestMcpSecurityScanner
{
    /// <summary>An <see cref="IMcpSecurityScanner"/> that reports every scan as safe.</summary>
    public static IMcpSecurityScanner AlwaysSafe() =>
        Mock.Of<IMcpSecurityScanner>(s =>
            s.ScanContent(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()) == McpToolScanResult.Safe("safe"));

    /// <summary>An <see cref="IOptionsMonitor{AIConfig}"/> exposing a default (governance-off) <see cref="AIConfig"/>.</summary>
    public static IOptionsMonitor<AIConfig> DefaultConfig() =>
        Mock.Of<IOptionsMonitor<AIConfig>>(m => m.CurrentValue == new AIConfig());
}
