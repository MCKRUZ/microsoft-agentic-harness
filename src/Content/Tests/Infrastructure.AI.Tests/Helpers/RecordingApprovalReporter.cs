using Application.AI.Common.Interfaces.Escalation;
using Application.AI.Common.Services.Escalation;
using Application.AI.Common.Services.Tools;
using Domain.AI.Escalation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Infrastructure.AI.Tests.Helpers;

/// <summary>
/// An <see cref="IApprovalExecutionReporter"/> that records every report, for tests of the raising
/// sites that report through <see cref="ScopedApprovalExecutionReporter"/> (#395).
/// </summary>
internal sealed class RecordingApprovalReporter : IApprovalExecutionReporter
{
    private readonly List<Report> _reports = [];

    /// <summary>One report: which escalation, how it ended, and what the site said.</summary>
    internal sealed record Report(
        Guid EscalationId,
        EscalationExecutionStatus Status,
        string ReportedBy,
        string? FailureText = null,
        EscalationNotExecutedReason? NotExecutedReason = null,
        bool HadFailureKey = false);

    /// <summary>Every report so far, in order.</summary>
    public IReadOnlyList<Report> Reports
    {
        get { lock (_reports) return [.. _reports]; }
    }

    /// <summary>A scope factory whose scopes resolve this reporter, as a host's container would.</summary>
    public IServiceScopeFactory ScopeFactory() => ScopeFactoryResolving(this);

    /// <summary>The reporter a long-lived site builds, reporting into this recorder.</summary>
    public ScopedApprovalExecutionReporter AsScoped() =>
        new(ScopeFactory(), NullLogger.Instance);

    /// <summary>A scope factory whose scopes resolve <paramref name="reporter"/>, or nothing when it is null.</summary>
    public static IServiceScopeFactory ScopeFactoryResolving(IApprovalExecutionReporter? reporter)
    {
        var provider = new Mock<IServiceProvider>();
        provider.Setup(p => p.GetService(typeof(IApprovalExecutionReporter))).Returns(reporter);
        var scope = new Mock<IServiceScope>();
        scope.SetupGet(s => s.ServiceProvider).Returns(provider.Object);
        var factory = new Mock<IServiceScopeFactory>();
        factory.Setup(f => f.CreateScope()).Returns(scope.Object);
        return factory.Object;
    }

    public ValueTask ReportSucceededAsync(ApprovedCall call, string reportedBy, CancellationToken ct) =>
        Add(ct, new Report(call.EscalationId, EscalationExecutionStatus.Succeeded, reportedBy, HadFailureKey: call.Key is not null));

    public ValueTask ReportFailedAsync(
        ApprovedCall call, PreparedFailureText failureReason, string reportedBy, CancellationToken ct) =>
        Add(ct, new Report(call.EscalationId, EscalationExecutionStatus.Failed, reportedBy,
            FailureText: failureReason.Text, HadFailureKey: call.Key is not null));

    public ValueTask ReportNotExecutedAsync(
        ApprovedCall call, EscalationNotExecutedReason reason, string reportedBy, CancellationToken ct) =>
        Add(ct, new Report(call.EscalationId, EscalationExecutionStatus.NeverExecuted, reportedBy,
            NotExecutedReason: reason, HadFailureKey: call.Key is not null));

    // A report made on a cancelled token is lost, as it would be with the real reporter (its audit write
    // is cancelled), so a site that reports on the wrong token shows up as a missing report.
    private ValueTask Add(CancellationToken ct, Report report)
    {
        if (ct.IsCancellationRequested)
            return ValueTask.CompletedTask;

        lock (_reports) _reports.Add(report);
        return ValueTask.CompletedTask;
    }
}
