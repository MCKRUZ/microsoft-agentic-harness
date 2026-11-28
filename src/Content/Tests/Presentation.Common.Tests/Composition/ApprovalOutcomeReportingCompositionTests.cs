using Application.AI.Common.Interfaces.Agents;
using Application.AI.Common.Interfaces.Escalation;
using Application.AI.Common.Interfaces.Orchestration.Magentic;
using Application.AI.Common.Services.Escalation;
using Domain.AI.Escalation;
using FluentAssertions;
using Infrastructure.AI.Agents;
using Infrastructure.AI.Orchestration.Magentic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Presentation.Common.Tests.Composition;

/// <summary>
/// #395 on the real composition root: the delegation supervisor and the Magentic orchestrator are
/// singletons, and the reporter they report through is scoped, so each report opens its own scope. These
/// tests prove that path reaches the production reporter — audited and sent to the approver — in the
/// graph real hosts build, rather than quietly resolving nothing.
/// </summary>
/// <remarks>
/// Only the audit store and the notifier are replaced, and only to observe them: they are where the
/// report lands, and the production audit store writes to disk.
/// </remarks>
public sealed class ApprovalOutcomeReportingCompositionTests
{
    [Fact]
    public async Task BothLongLivedReportingSites_ResolveFromTheProductionGraph()
    {
        // Async disposal: the graph holds services that only implement IAsyncDisposable.
        await using var provider = CompositionRootTestHost.BuildProvider([]);

        provider.GetRequiredService<ISupervisor>().Should().BeOfType<CapabilityMatchSupervisor>();
        provider.GetRequiredService<IMagenticOrchestrator>().Should().BeOfType<MagenticOrchestrator>();
    }

    [Fact]
    public async Task AReportFromANewScope_IsAuditedAndSentToTheApprover_ByTheProductionReporter()
    {
        var audit = new Mock<IEscalationAuditStore>();
        EscalationExecutionRecord? audited = null;
        audit.Setup(a => a.RecordExecutionAsync(It.IsAny<EscalationExecutionRecord>(), It.IsAny<CancellationToken>()))
            .Callback<EscalationExecutionRecord, CancellationToken>((record, _) => audited = record)
            .Returns(Task.CompletedTask);
        var notifier = new Mock<IEscalationNotifier>();
        await using var provider = CompositionRootTestHost.BuildProvider([], (services, _) =>
        {
            services.AddSingleton(audit.Object);
            services.AddSingleton(notifier.Object);
        });
        var reporter = new ScopedApprovalExecutionReporter(
            provider.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance);
        var escalationId = Guid.NewGuid();

        await reporter.ReportNotExecutedAsync(
            escalationId, EscalationNotExecutedReason.NoAgentAvailable, "delegation", CancellationToken.None);

        audited.Should().NotBeNull("the scoped reporter must resolve on the graph hosts build");
        audited!.EscalationId.Should().Be(escalationId);
        audited.Status.Should().Be(EscalationExecutionStatus.NeverExecuted);
        audited.NotExecutedReason.Should().Be(EscalationNotExecutedReason.NoAgentAvailable);
        audited.ReportedBy.Should().Be("delegation");
        notifier.Verify(n => n.NotifyExecutionReportedAsync(audited, It.IsAny<CancellationToken>()), Times.Once);
    }
}
