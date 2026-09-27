using System.Globalization;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Agent;
using Domain.AI.Agents;
using Domain.Common;
using Domain.Common.Models;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Application.Core.CQRS.Agents.RefreshAgentRegistry;

/// <summary>
/// Forces an immediate agent registry rescan via <see cref="IAgentRegistryRefresher.Refresh"/> and
/// records the request in the audit trail with the token-derived caller identity.
/// </summary>
/// <remarks>
/// <para>
/// Uses <see cref="IAuditSink"/> directly rather than the <c>AuditTrailBehavior</c> MediatR pipeline
/// (the mechanism <c>IAuditable</c> commands opt into): that behaviour resolves
/// <c>IAgentExecutionContext</c> for the executor id, which is scoped to an agent conversation turn
/// and has no meaningful value for an HTTP-triggered operator command with no conversation behind
/// it. Mirrors the shape of <c>DriftOperatorAuditRecorder</c> — an attempt record before the work, an
/// outcome record after — but against the general-purpose sink rather than building a dedicated
/// audit store, since a registry refresh carries none of the drift subsystem's history-poisoning
/// threat model that store exists to defend against.
/// </para>
/// <para>
/// <b>Fail posture, and why it differs from Drift's.</b> The attempt record is best-effort, not a
/// precondition: unlike a drift baseline recalculation (which destroys the previous snapshot and
/// re-anchors what "normal" means), a registry refresh only re-reads files already on disk and is
/// trivially repeatable, so refusing an operational cache refresh because a log write failed would
/// make the audit trail a liability rather than a safeguard. Both records are logged on failure so a
/// systemically broken sink is still visible in server logs.
/// </para>
/// </remarks>
public sealed class RefreshAgentRegistryCommandHandler
    : IRequestHandler<RefreshAgentRegistryCommand, Result<AgentRegistryRefreshResult>>
{
    private const string RefreshAction = "agent_registry.refresh";

    private readonly IAgentRegistryRefresher _refresher;
    private readonly IAuditSink _auditSink;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RefreshAgentRegistryCommandHandler> _logger;

    /// <summary>Initializes a new instance of the <see cref="RefreshAgentRegistryCommandHandler"/> class.</summary>
    /// <param name="refresher">The registry reload seam this handler drives.</param>
    /// <param name="auditSink">The audit sink the request and its outcome are recorded to.</param>
    /// <param name="timeProvider">Time provider for audit timestamps.</param>
    /// <param name="logger">Logger for refresh diagnostics.</param>
    public RefreshAgentRegistryCommandHandler(
        IAgentRegistryRefresher refresher,
        IAuditSink auditSink,
        TimeProvider timeProvider,
        ILogger<RefreshAgentRegistryCommandHandler> logger)
    {
        _refresher = refresher;
        _auditSink = auditSink;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result<AgentRegistryRefreshResult>> Handle(
        RefreshAgentRegistryCommand request, CancellationToken cancellationToken)
    {
        await RecordSafelyAsync(request, AuditOutcome.Success, summary: null, failureReason: null,
            phase: "attempt", cancellationToken);

        AgentRegistryRefreshResult summary;
        try
        {
            summary = _refresher.Refresh();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Agent registry refresh requested by {CallerId} failed", request.CallerId);
            await RecordSafelyAsync(request, AuditOutcome.Failure, summary: null, ex.GetType().Name,
                phase: "outcome", cancellationToken);
            return Result<AgentRegistryRefreshResult>.Fail("agent_registry.refresh_failed");
        }

        _logger.LogInformation(
            "Agent registry refreshed by {CallerId}: {Added} added, {Updated} updated, {Removed} removed, {Total} total",
            request.CallerId, summary.Added.Count, summary.Updated.Count, summary.Removed.Count, summary.TotalAgentCount);

        await RecordSafelyAsync(request, AuditOutcome.Success, summary, failureReason: null,
            phase: "outcome", cancellationToken);

        return Result<AgentRegistryRefreshResult>.Success(summary);
    }

    private async Task RecordSafelyAsync(
        RefreshAgentRegistryCommand request,
        AuditOutcome outcome,
        AgentRegistryRefreshResult? summary,
        string? failureReason,
        string phase,
        CancellationToken cancellationToken)
    {
        try
        {
            await _auditSink.RecordAsync(new AuditEntry
            {
                RequestType = nameof(RefreshAgentRegistryCommand),
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
                "Failed to append agent registry refresh {Phase} audit (requested by {CallerId})",
                phase, request.CallerId);
        }
    }

    private static IReadOnlyDictionary<string, string> BuildMetadata(string phase, AgentRegistryRefreshResult? summary)
    {
        var metadata = new Dictionary<string, string> { ["phase"] = phase };

        if (summary is null)
            return metadata;

        metadata["added"] = summary.Added.Count.ToString(CultureInfo.InvariantCulture);
        metadata["updated"] = summary.Updated.Count.ToString(CultureInfo.InvariantCulture);
        metadata["removed"] = summary.Removed.Count.ToString(CultureInfo.InvariantCulture);
        metadata["total"] = summary.TotalAgentCount.ToString(CultureInfo.InvariantCulture);
        return metadata;
    }
}
