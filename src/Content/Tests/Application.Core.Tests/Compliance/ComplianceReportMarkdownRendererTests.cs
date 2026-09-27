using Application.Core.Compliance;
using Domain.AI.Compliance;
using FluentAssertions;
using Xunit;

namespace Application.Core.Tests.Compliance;

/// <summary>Tests for <see cref="ComplianceReportMarkdownRenderer"/>.</summary>
public sealed class ComplianceReportMarkdownRendererTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    private static ComplianceReport EmptyReport(IReadOnlyList<string>? warnings = null) => new()
    {
        GeneratedAt = Now,
        GeneratedBy = "operator-1",
        PeriodStart = Now.AddDays(-7),
        PeriodEnd = Now,
        Sessions = new ComplianceSessionSummary
        {
            TotalSessions = 0, CompletedSessions = 0, ErroredSessions = 0, CancelledSessions = 0,
            TotalCostUsd = 0, TotalInputTokens = 0, TotalOutputTokens = 0,
        },
        Safety = new ComplianceSafetySummary
        {
            TotalEvents = 0, BlockedCount = 0, RedactedCount = 0, CountsByCategory = new Dictionary<string, int>(),
        },
        SafetyEvents = [],
        AuditEntries = [],
        GovernanceDecisions = [],
        ChangeDecisions = [],
        EgressDecisions = [],
        EscalationEvents = [],
        DriftFindings = [],
        ChainIntegrity = [],
        Warnings = warnings ?? [],
    };

    [Fact]
    public void Render_EmptyReport_ProducesReadableMarkdownWithNoWarningsSection()
    {
        var markdown = ComplianceReportMarkdownRenderer.Render(EmptyReport());

        markdown.Should().Contain("# Compliance Report");
        markdown.Should().Contain("operator-1");
        markdown.Should().NotContain("Warnings — this report is incomplete");
        markdown.Should().Contain("_None in this period._");
    }

    [Fact]
    public void Render_ReportWithWarnings_SurfacesThemProminently()
    {
        var markdown = ComplianceReportMarkdownRenderer.Render(
            EmptyReport(warnings: ["Failed to read safety events: database unavailable"]));

        markdown.Should().Contain("Warnings — this report is incomplete");
        markdown.Should().Contain("Failed to read safety events: database unavailable");
    }

    [Fact]
    public void Render_ScopedToConversation_NamesTheConversation()
    {
        var report = EmptyReport() with { ConversationId = "conv-a" };

        var markdown = ComplianceReportMarkdownRenderer.Render(report);

        markdown.Should().Contain("Conversation `conv-a`");
    }

    [Fact]
    public void Render_BrokenChain_ShowsFailureReason()
    {
        var report = EmptyReport() with
        {
            ChainIntegrity =
            [
                new ComplianceChainIntegrity
                {
                    ChainName = "governance", IsValid = false, VerifiedCount = 10,
                    FailureReason = "record-hash mismatch",
                },
            ],
        };

        var markdown = ComplianceReportMarkdownRenderer.Render(report);

        markdown.Should().Contain("governance");
        markdown.Should().Contain("BROKEN");
        markdown.Should().Contain("record-hash mismatch");
    }

    [Fact]
    public void Render_NullReport_Throws()
    {
        var act = () => ComplianceReportMarkdownRenderer.Render(null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
