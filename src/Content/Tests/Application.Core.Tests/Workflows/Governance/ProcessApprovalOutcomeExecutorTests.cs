using Application.AI.Common.Interfaces.Audit;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Telemetry;
using Application.Core.Workflows.Governance;
using Domain.AI.Telemetry.Redaction;
using FluentAssertions;
using Microsoft.Agents.AI.Workflows;
using Moq;
using Xunit;

namespace Application.Core.Tests.Workflows.Governance;

/// <summary>
/// <see cref="ProcessApprovalOutcomeExecutor"/> writes the approver's decision into the hash-chained governance trail
/// (#903): the approver as the audit reference, the reason redacted, never the name itself.
/// </summary>
public sealed class ProcessApprovalOutcomeExecutorTests
{
    private readonly Mock<IGovernanceAuditService> _audit = new();
    private readonly Mock<IAuditPseudonymizer> _pseudonymizer = new();
    private readonly Mock<IContentRedactionFilter> _redaction = new();

    public ProcessApprovalOutcomeExecutorTests()
    {
        _pseudonymizer.Setup(p => p.Person(It.IsAny<string>())).Returns<string>(name => $"ref:k1:{name.Length:x8}");
        _redaction.Setup(r => r.Redact(It.IsAny<string?>(), RedactionCategories.All))
            .Returns<string?, IReadOnlyList<RedactionCategory>>((text, _) => text!.Replace("alice@contoso.com", "[REDACTED:Email]"));
    }

    private string Recorded(ApprovalResponse response)
    {
        string? entry = null;
        _audit.Setup(a => a.Log("agent-1", "deploy", It.IsAny<string>()))
            .Callback<string, string, string>((_, _, decision) => entry = decision);
        var executor = new ProcessApprovalOutcomeExecutor(_audit.Object, _pseudonymizer.Object, _redaction.Object);

        executor.HandleAsync(response, Mock.Of<IWorkflowContext>()).AsTask().GetAwaiter().GetResult();

        return entry!;
    }

    [Fact]
    public void HandleAsync_RecordsTheApproverAsTheAuditReferenceAndTheReasonRedacted()
    {
        var entry = Recorded(new ApprovalResponse(
            true, "bob@contoso.com", "alice@contoso.com asked for it", DateTimeOffset.UtcNow, "agent-1", "deploy"));

        entry.Should().Be("RequireApproval:Approved|By:ref:k1:0000000f|Reason:[REDACTED:Email] asked for it");
        entry.Should().NotContain("@contoso.com");
    }

    [Fact]
    public void HandleAsync_NoReason_RecordsNoneAndRedactsNothing()
    {
        var entry = Recorded(new ApprovalResponse(false, "bob", null, DateTimeOffset.UtcNow, "agent-1", "deploy"));

        entry.Should().Be("RequireApproval:Denied|By:ref:k1:00000003|Reason:none");
        _redaction.VerifyNoOtherCalls();
    }
}
