using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Audit;
using Application.AI.Common.Interfaces.Changes;
using Application.AI.Common.Interfaces.DriftDetection;
using Application.AI.Common.Interfaces.Egress;
using Application.AI.Common.Interfaces.Escalation;
using Application.AI.Common.Interfaces.Governance;
using Domain.AI.Compliance;
using Domain.AI.Observability.Models;
using Domain.Common;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Application.Core.CQRS.Compliance.GenerateComplianceReport;

/// <summary>
/// Builds a <see cref="ComplianceReport"/> by fanning out, concurrently, across every audit-chain
/// store built in #714 (governance, change, egress, escalation, drift), the conversation
/// database's session/safety/audit-log reads, and a live tamper-evidence check of every
/// hash-chained trail (#696).
/// </summary>
/// <remarks>
/// <para>
/// Reads the five audit-chain stores directly rather than dispatching their own
/// <c>Get*AuditsQuery</c>/<c>QueryEscalationAuditsQuery</c> through <see cref="IMediator"/>:
/// this handler already runs inside the MediatR pipeline, and nested <c>Send</c> calls launched
/// concurrently via <see cref="Task.WhenAll(Task[])"/> corrupt <c>AsyncLocal</c>-scoped pipeline
/// state shared across the sibling dispatches (the same reentrancy hazard
/// <c>TrainSkillCommandHandler</c> avoids by chaining its own stages directly instead of
/// re-entering MediatR). Reading the store interfaces directly sidesteps the whole pipeline for
/// these five internal reads, at the cost of re-applying each query's own most-recent-N capping
/// here instead of reusing its handler.
/// </para>
/// <para>
/// A source that fails is never silently reported as empty: its failure lands in
/// <see cref="ComplianceReport.Warnings"/> and the section is empty specifically because the read
/// failed, not because there was nothing to report.
/// </para>
/// </remarks>
public sealed class GenerateComplianceReportQueryHandler
    : IRequestHandler<GenerateComplianceReportQuery, Result<ComplianceReport>>
{
    private readonly IObservabilityStore _observabilityStore;
    private readonly IGovernanceAuditService _governanceAuditService;
    private readonly IChangeAuditWriter _changeAuditWriter;
    private readonly IEgressAuditWriter _egressAuditWriter;
    private readonly IEscalationAuditStore _escalationAuditStore;
    private readonly IDriftAuditStore _driftAuditStore;
    private readonly IEnumerable<IVerifiableAuditChain> _chains;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<GenerateComplianceReportQueryHandler> _logger;

    /// <summary>Initializes a new instance of the <see cref="GenerateComplianceReportQueryHandler"/> class.</summary>
    public GenerateComplianceReportQueryHandler(
        IObservabilityStore observabilityStore,
        IGovernanceAuditService governanceAuditService,
        IChangeAuditWriter changeAuditWriter,
        IEgressAuditWriter egressAuditWriter,
        IEscalationAuditStore escalationAuditStore,
        IDriftAuditStore driftAuditStore,
        IEnumerable<IVerifiableAuditChain> chains,
        TimeProvider timeProvider,
        ILogger<GenerateComplianceReportQueryHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(observabilityStore);
        ArgumentNullException.ThrowIfNull(governanceAuditService);
        ArgumentNullException.ThrowIfNull(changeAuditWriter);
        ArgumentNullException.ThrowIfNull(egressAuditWriter);
        ArgumentNullException.ThrowIfNull(escalationAuditStore);
        ArgumentNullException.ThrowIfNull(driftAuditStore);
        ArgumentNullException.ThrowIfNull(chains);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _observabilityStore = observabilityStore;
        _governanceAuditService = governanceAuditService;
        _changeAuditWriter = changeAuditWriter;
        _egressAuditWriter = egressAuditWriter;
        _escalationAuditStore = escalationAuditStore;
        _driftAuditStore = driftAuditStore;
        _chains = chains;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result<ComplianceReport>> Handle(
        GenerateComplianceReportQuery request, CancellationToken cancellationToken)
    {
        var sessionsTask = _observabilityStore.GetSessionsAsync(
            request.MaxRecordsPerSource, 0, status: null, request.Start, request.End, cancellationToken);
        var safetyTask = _observabilityStore.GetSafetyEventsAsync(
            request.Start, request.End, outcome: null, request.MaxRecordsPerSource, 0, cancellationToken);
        var auditTask = _observabilityStore.GetAuditEntriesAsync(
            request.Start, request.End, source: null, request.MaxRecordsPerSource, 0, cancellationToken);
        var governanceTask = _governanceAuditService.GetRecordsAsync(
            new GovernanceAuditQuery { Start = request.Start, End = request.End }, cancellationToken);
        var changeTask = _changeAuditWriter.GetRecordsAsync(
            new ChangeAuditQuery { Start = request.Start, End = request.End }, cancellationToken);
        var egressTask = _egressAuditWriter.GetRecordsAsync(
            new EgressAuditQuery { Start = request.Start, End = request.End }, cancellationToken);
        var escalationTask = _escalationAuditStore.QueryAsync(
            new EscalationAuditQuery { Start = request.Start, End = request.End }, cancellationToken);
        var driftTask = _driftAuditStore.GetRecordsAsync(
            new DriftAuditQuery { Start = request.Start, End = request.End }, cancellationToken);
        var chainIntegrityTask = VerifyAllChainsAsync(cancellationToken);

        await Task.WhenAll(
            sessionsTask, safetyTask, auditTask, governanceTask, changeTask,
            egressTask, escalationTask, driftTask, chainIntegrityTask).ConfigureAwait(false);

        var warnings = new List<string>();

        IReadOnlyList<SessionRecord> sessions = sessionsTask.Result;
        HashSet<Guid>? scopedSessionIds = null;
        if (!string.IsNullOrEmpty(request.ConversationId))
        {
            sessions = sessions.Where(s => s.ConversationId == request.ConversationId).ToList();
            scopedSessionIds = sessions.Select(s => s.Id).ToHashSet();
        }

        var safetyEvents = UnwrapAndCap(safetyTask.Result, "safety events", request.MaxRecordsPerSource, warnings);
        if (scopedSessionIds is not null)
            safetyEvents = safetyEvents.Where(e => scopedSessionIds.Contains(e.SessionId)).ToList();

        var auditEntries = UnwrapAndCap(auditTask.Result, "audit log entries", request.MaxRecordsPerSource, warnings);
        if (scopedSessionIds is not null)
            auditEntries = auditEntries
                .Where(e => e.SessionId.HasValue && scopedSessionIds.Contains(e.SessionId.Value))
                .ToList();

        var governance = UnwrapAndCap(governanceTask.Result, "governance decisions", request.MaxRecordsPerSource, warnings);
        var change = UnwrapAndCap(changeTask.Result, "change-proposal decisions", request.MaxRecordsPerSource, warnings);
        var egress = UnwrapAndCap(egressTask.Result, "egress decisions", request.MaxRecordsPerSource, warnings);
        var escalation = UnwrapAndCap(escalationTask.Result, "escalation events", request.MaxRecordsPerSource, warnings);
        var drift = UnwrapAndCap(driftTask.Result, "drift findings", request.MaxRecordsPerSource, warnings);

        var report = new ComplianceReport
        {
            GeneratedAt = _timeProvider.GetUtcNow(),
            GeneratedBy = request.CallerId,
            PeriodStart = request.Start,
            PeriodEnd = request.End,
            ConversationId = request.ConversationId,
            Sessions = BuildSessionSummary(sessions),
            Safety = BuildSafetySummary(safetyEvents),
            SafetyEvents = safetyEvents,
            AuditEntries = auditEntries,
            GovernanceDecisions = governance,
            ChangeDecisions = change,
            EgressDecisions = egress,
            EscalationEvents = escalation,
            DriftFindings = drift,
            ChainIntegrity = chainIntegrityTask.Result,
            Warnings = warnings,
        };

        if (warnings.Count > 0)
            _logger.LogWarning(
                "Compliance report for {CallerId} ({Start} to {End}) generated with {WarningCount} source failure(s): {Warnings}",
                request.CallerId, request.Start, request.End, warnings.Count, string.Join(" | ", warnings));

        await _observabilityStore.RecordAuditAsync(
            "compliance_report_generated",
            "harness",
            sessionId: null,
            metadata: new Dictionary<string, object>
            {
                ["caller_id"] = request.CallerId,
                ["period_start"] = request.Start.ToString("O"),
                ["period_end"] = request.End.ToString("O"),
                ["conversation_id"] = request.ConversationId ?? "(all)",
                ["warning_count"] = warnings.Count,
            },
            cancellationToken).ConfigureAwait(false);

        return Result<ComplianceReport>.Success(report);
    }

    private async Task<IReadOnlyList<ComplianceChainIntegrity>> VerifyAllChainsAsync(
        CancellationToken cancellationToken)
    {
        var results = new List<ComplianceChainIntegrity>();
        foreach (var chain in _chains)
        {
            var verification = await chain.VerifyChainAsync(cancellationToken).ConfigureAwait(false);
            results.Add(new ComplianceChainIntegrity
            {
                ChainName = chain.AuditName,
                IsValid = verification.IsValid,
                VerifiedCount = verification.VerifiedCount,
                FailureReason = verification.FailureReason,
            });
        }

        return results;
    }

    /// <summary>
    /// Unwraps a store's <see cref="Result{T}"/>, recording a warning on failure, and — mirroring
    /// each trail's own <c>Get*AuditsQueryHandler</c> — caps a successful result to the most recent
    /// <paramref name="maxRecords"/> entries (the stores themselves return every match in the
    /// window, unbounded).
    /// </summary>
    private static IReadOnlyList<T> UnwrapAndCap<T>(
        Result<IReadOnlyList<T>> result, string sourceName, int maxRecords, List<string> warnings)
    {
        if (!result.IsSuccess)
        {
            warnings.Add($"Failed to read {sourceName}: {string.Join("; ", result.Errors)}");
            return [];
        }

        var records = result.Value!;
        return records.Count <= maxRecords
            ? records
            : records.Skip(records.Count - maxRecords).ToList();
    }

    private static ComplianceSessionSummary BuildSessionSummary(IReadOnlyList<SessionRecord> sessions) => new()
    {
        TotalSessions = sessions.Count,
        CompletedSessions = sessions.Count(s => s.Status == "completed"),
        ErroredSessions = sessions.Count(s => s.Status == "error"),
        CancelledSessions = sessions.Count(s => s.Status == "cancelled"),
        TotalCostUsd = sessions.Sum(s => s.TotalCostUsd),
        TotalInputTokens = sessions.Sum(s => (long)s.TotalInputTokens),
        TotalOutputTokens = sessions.Sum(s => (long)s.TotalOutputTokens),
    };

    private static ComplianceSafetySummary BuildSafetySummary(IReadOnlyList<SafetyEventRecord> events) => new()
    {
        TotalEvents = events.Count,
        BlockedCount = events.Count(e => e.Outcome == "block"),
        RedactedCount = events.Count(e => e.Outcome == "redact"),
        CountsByCategory = events
            .Where(e => e.Category is not null && e.Outcome != "pass")
            .GroupBy(e => e.Category!)
            .ToDictionary(g => g.Key, g => g.Count()),
    };
}
