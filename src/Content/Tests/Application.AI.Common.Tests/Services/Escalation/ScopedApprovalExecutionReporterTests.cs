using Application.AI.Common.Interfaces.Escalation;
using Application.AI.Common.Services.Escalation;
using Application.AI.Common.Services.Tools;
using Domain.AI.Escalation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.Services.Escalation;

/// <summary>
/// <see cref="ScopedApprovalExecutionReporter"/> (#395) reports from long-lived services by opening a scope
/// per report. It must never throw, including in the steps before the reporter's own guard: an exception
/// here would turn an approved action that succeeded into an error its caller may retry.
/// </summary>
public sealed class ScopedApprovalExecutionReporterTests
{
    private static IServiceScopeFactory ScopeFactoryResolving(IApprovalExecutionReporter? reporter)
    {
        var provider = new Mock<IServiceProvider>();
        provider.Setup(p => p.GetService(typeof(IApprovalExecutionReporter))).Returns(reporter);
        var scope = new Mock<IServiceScope>();
        scope.SetupGet(s => s.ServiceProvider).Returns(provider.Object);
        var factory = new Mock<IServiceScopeFactory>();
        factory.Setup(f => f.CreateScope()).Returns(scope.Object);
        return factory.Object;
    }

    private static ScopedApprovalExecutionReporter Create(IServiceScopeFactory factory) =>
        new(factory, NullLogger.Instance);

    [Fact]
    public async Task ReportSucceededAsync_ReachesTheScopedReporter_WithNoFailureKey()
    {
        var reporter = new Mock<IApprovalExecutionReporter>();
        var id = Guid.NewGuid();

        await Create(ScopeFactoryResolving(reporter.Object)).ReportSucceededAsync(id, "delegation", CancellationToken.None);

        reporter.Verify(r => r.ReportSucceededAsync(
            new ApprovedCall(id, null), "delegation", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ReportFailedAsync_PassesTheSitesTextAsItIs()
    {
        var reporter = new Mock<IApprovalExecutionReporter>();
        var id = Guid.NewGuid();

        await Create(ScopeFactoryResolving(reporter.Object))
            .ReportFailedAsync(id, "Delegation timed out.", "delegation", CancellationToken.None);

        reporter.Verify(r => r.ReportFailedAsync(
            new ApprovedCall(id, null),
            new PreparedFailureText("Delegation timed out.", FailureTextSubstitution.None),
            "delegation",
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ReportNotExecutedAsync_PassesTheReason()
    {
        var reporter = new Mock<IApprovalExecutionReporter>();
        var id = Guid.NewGuid();

        await Create(ScopeFactoryResolving(reporter.Object)).ReportNotExecutedAsync(
            id, EscalationNotExecutedReason.NoAgentAvailable, "delegation", CancellationToken.None);

        reporter.Verify(r => r.ReportNotExecutedAsync(
            new ApprovedCall(id, null), EscalationNotExecutedReason.NoAgentAvailable, "delegation",
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AHostWithNoReporterRegistered_DoesNotThrow()
    {
        var exception = await Record.ExceptionAsync(() =>
            Create(ScopeFactoryResolving(null)).ReportSucceededAsync(Guid.NewGuid(), "delegation", CancellationToken.None));

        Assert.Null(exception);
    }

    [Fact]
    public async Task OpeningTheScopeThrows_DoesNotThrow()
    {
        // A disposed root provider at shutdown fails here, before the reporter's own guard is reached.
        var factory = new Mock<IServiceScopeFactory>();
        factory.Setup(f => f.CreateScope()).Throws(new ObjectDisposedException("IServiceProvider"));

        var exception = await Record.ExceptionAsync(() =>
            Create(factory.Object).ReportSucceededAsync(Guid.NewGuid(), "delegation", CancellationToken.None));

        Assert.Null(exception);
    }

    [Fact]
    public async Task AReporterThatBreaksItsOwnContract_DoesNotThrowThroughHere()
    {
        // The default reporter never throws, but a host may register its own.
        var reporter = new Mock<IApprovalExecutionReporter>();
        reporter.Setup(r => r.ReportSucceededAsync(It.IsAny<ApprovedCall>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("audit store down"));

        var exception = await Record.ExceptionAsync(() =>
            Create(ScopeFactoryResolving(reporter.Object)).ReportSucceededAsync(Guid.NewGuid(), "delegation", CancellationToken.None));

        Assert.Null(exception);
    }

    [Fact]
    public async Task AReportThatHangs_IsAbandonedAtTheTimeLimit_SoTheCallerIsNotHeldUp()
    {
        var reporter = new Mock<IApprovalExecutionReporter>();
        reporter.Setup(r => r.ReportSucceededAsync(It.IsAny<ApprovedCall>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<ApprovedCall, string, CancellationToken>(
                async (_, _, ct) => await Task.Delay(Timeout.InfiniteTimeSpan, ct));
        var scoped = new ScopedApprovalExecutionReporter(
            ScopeFactoryResolving(reporter.Object), NullLogger.Instance, TimeSpan.FromMilliseconds(100));

        var report = scoped.ReportSucceededAsync(Guid.NewGuid(), "delegation", CancellationToken.None);
        var finished = await Task.WhenAny(report, Task.Delay(TimeSpan.FromSeconds(30)));

        Assert.Same(report, finished);
        await report;
    }
}
