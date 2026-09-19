using System.Globalization;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Agent;
using Domain.AI.Skills;
using Domain.Common;
using Domain.Common.Models;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Application.Core.CQRS.Skills.RefreshSkillRegistry;

/// <summary>
/// Forces an immediate skill registry rescan via <see cref="ISkillRegistryRefresher.Refresh"/> and
/// records the request in the audit trail with the token-derived caller identity.
/// </summary>
/// <remarks>
/// Mirrors <c>Application.Core.CQRS.Agents.RefreshAgentRegistry.RefreshAgentRegistryCommandHandler</c>
/// from issue #705, including its fail-open audit posture: the attempt record is best-effort, not a
/// precondition — a skill registry refresh only re-reads files already on disk and is trivially
/// repeatable, so refusing it because a log write failed would make the audit trail a liability
/// rather than a safeguard. Both records are logged on failure so a systemically broken sink is
/// still visible in server logs.
/// </remarks>
public sealed class RefreshSkillRegistryCommandHandler
    : IRequestHandler<RefreshSkillRegistryCommand, Result<SkillRegistryRefreshResult>>
{
    private const string RefreshAction = "skill_registry.refresh";

    private readonly ISkillRegistryRefresher _refresher;
    private readonly IAuditSink _auditSink;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RefreshSkillRegistryCommandHandler> _logger;

    /// <summary>Initializes a new instance of the <see cref="RefreshSkillRegistryCommandHandler"/> class.</summary>
    /// <param name="refresher">The registry reload seam this handler drives.</param>
    /// <param name="auditSink">The audit sink the request and its outcome are recorded to.</param>
    /// <param name="timeProvider">Time provider for audit timestamps.</param>
    /// <param name="logger">Logger for refresh diagnostics.</param>
    public RefreshSkillRegistryCommandHandler(
        ISkillRegistryRefresher refresher,
        IAuditSink auditSink,
        TimeProvider timeProvider,
        ILogger<RefreshSkillRegistryCommandHandler> logger)
    {
        _refresher = refresher;
        _auditSink = auditSink;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result<SkillRegistryRefreshResult>> Handle(
        RefreshSkillRegistryCommand request, CancellationToken cancellationToken)
    {
        await RecordSafelyAsync(request, AuditOutcome.Success, summary: null, failureReason: null,
            phase: "attempt", cancellationToken);

        SkillRegistryRefreshResult summary;
        try
        {
            summary = _refresher.Refresh();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Skill registry refresh requested by {CallerId} failed", request.CallerId);
            await RecordSafelyAsync(request, AuditOutcome.Failure, summary: null, ex.GetType().Name,
                phase: "outcome", cancellationToken);
            return Result<SkillRegistryRefreshResult>.Fail("skill_registry.refresh_failed");
        }

        _logger.LogInformation(
            "Skill registry refreshed by {CallerId}: {Added} added, {Updated} updated, {Removed} removed, {Total} total",
            request.CallerId, summary.Added.Count, summary.Updated.Count, summary.Removed.Count, summary.TotalSkillCount);

        await RecordSafelyAsync(request, AuditOutcome.Success, summary, failureReason: null,
            phase: "outcome", cancellationToken);

        return Result<SkillRegistryRefreshResult>.Success(summary);
    }

    private async Task RecordSafelyAsync(
        RefreshSkillRegistryCommand request,
        AuditOutcome outcome,
        SkillRegistryRefreshResult? summary,
        string? failureReason,
        string phase,
        CancellationToken cancellationToken)
    {
        try
        {
            await _auditSink.RecordAsync(new AuditEntry
            {
                RequestType = nameof(RefreshSkillRegistryCommand),
                Action = RefreshAction,
                ExecutorId = request.CallerId,
                Timestamp = _timeProvider.GetUtcNow(),
                Outcome = outcome,
                FailureReason = failureReason,
                Metadata = BuildMetadata(phase, summary)
            }, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex,
                "Failed to append skill registry refresh {Phase} audit (requested by {CallerId})",
                phase, request.CallerId);
        }
    }

    private static IReadOnlyDictionary<string, string> BuildMetadata(string phase, SkillRegistryRefreshResult? summary)
    {
        var metadata = new Dictionary<string, string> { ["phase"] = phase };

        if (summary is null)
            return metadata;

        metadata["added"] = summary.Added.Count.ToString(CultureInfo.InvariantCulture);
        metadata["updated"] = summary.Updated.Count.ToString(CultureInfo.InvariantCulture);
        metadata["removed"] = summary.Removed.Count.ToString(CultureInfo.InvariantCulture);
        metadata["total"] = summary.TotalSkillCount.ToString(CultureInfo.InvariantCulture);
        return metadata;
    }
}
