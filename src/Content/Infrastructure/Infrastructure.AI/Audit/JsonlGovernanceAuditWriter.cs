using System.Text.Json;
using Application.AI.Common.Interfaces.Audit;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.OpenTelemetry.Metrics;
using Domain.AI.Audit;
using Domain.AI.Telemetry.Conventions;
using Domain.Common.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Audit;

/// <summary>
/// Durable, tamper-evident <see cref="IGovernanceAuditService"/> backed by
/// <see cref="HashChainedJsonlWriter"/> — the same shared primitive behind the escalation, drift,
/// change, and egress audit trails. Replaces <c>AgtAuditAdapter</c>'s in-memory-only
/// <c>Microsoft.AgentGovernance.Audit.AuditLogger</c> (#407): the hash chain now survives a process
/// restart instead of growing unbounded for the process lifetime and vanishing on every restart.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The interface stays synchronous, deliberately.</strong> <see cref="IGovernanceAuditService.Log"/>
/// has 15 production call sites, several inside the tool-call admission chain's synchronous
/// <c>Blocked()</c>/<c>Audit()</c> helper methods (<c>ToolInvocationGovernor</c>,
/// <c>ToolCallObserverChain</c>, <c>DefaultToolClassificationGate</c>) — making the interface async
/// would cascade through ~25-30 edit points in the highest-risk code in the repo, for a durability
/// guarantee this design delivers without touching a single caller. <see cref="Log"/> blocks on
/// <see cref="HashChainedJsonlWriter.AppendAsync"/> (<c>GetAwaiter().GetResult()</c>) rather than
/// duplicating its ~150 lines of hash-chain/head-recovery logic synchronously. This is safe here
/// specifically: every host that calls this (ASP.NET Core, .NET console) has no captured
/// <see cref="SynchronizationContext"/>, so blocking on the task cannot deadlock — it only holds a
/// thread-pool thread for the sub-millisecond duration of a small file append on a low-frequency path.
/// <see cref="VerifyChainIntegrity"/> and <see cref="EntryCount"/> block the same way.
/// </para>
/// <para>
/// <strong>No member of this class throws.</strong> <see cref="Log"/>, <see cref="VerifyChainIntegrity"/>,
/// and <see cref="EntryCount"/> all catch broadly and degrade rather than propagate. For
/// <see cref="Log"/> specifically: unlike <c>JsonlEscalationAuditStore</c> (whose primary operation IS
/// the audit record, so a write failure legitimately propagates), every caller here treats the audit
/// write as a side effect of a decision already made — the tool call is already being allowed, denied,
/// or blocked before <c>Log</c> runs, and none of the 15 call sites today expects or handles an
/// exception from it. A disk failure here should degrade the audit trail's completeness, not turn a
/// clean deny into an unhandled exception on the tool-call path. The failure is logged via structured
/// logging instead — "the audit is the record, not the control" is the same policy
/// <c>TrainSkillCommandHandler</c>'s audit call sites already state explicitly.
/// </para>
/// <para>
/// <strong>A write failure is never silent, even though it never throws</strong> (a security-review
/// finding on #407's follow-up): <see cref="Log"/> increments
/// <c>GovernanceMetrics.AuditWriteFailures</c> on every failed append, alongside the structured log
/// line — a broken (non-blank) <c>AuditStoragePath</c> is caught by neither the boot-time
/// <c>NotEmpty</c> validator nor an exception, so the metric is the only signal an operator not
/// tailing logs ever gets that the audit trail has gone dark.
/// </para>
/// </remarks>
public sealed class JsonlGovernanceAuditWriter : IGovernanceAuditService, IVerifiableAuditChain, IDisposable
{
    private static readonly JsonSerializerOptions SerializeOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
    };

    private readonly string _filePath;
    private readonly HashChainedJsonlWriter _chain;
    private readonly ILogger<JsonlGovernanceAuditWriter> _logger;

    /// <summary>Initializes a new instance of <see cref="JsonlGovernanceAuditWriter"/>.</summary>
    /// <param name="config">Application configuration providing the audit storage path.</param>
    /// <param name="logger">Logger for operational diagnostics.</param>
    public JsonlGovernanceAuditWriter(
        IOptionsMonitor<AppConfig> config,
        ILogger<JsonlGovernanceAuditWriter> logger)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);

        _filePath = Path.Combine(config.CurrentValue.AI.Governance.AuditStoragePath, "governance.jsonl");
        _chain = new HashChainedJsonlWriter(_filePath, logger);
        _logger = logger;
    }

    /// <inheritdoc />
    public string AuditName => "governance";

    /// <inheritdoc />
    public void Log(string agentId, string action, string decision)
    {
        var record = new GovernanceAuditRecord
        {
            Timestamp = DateTimeOffset.UtcNow,
            AgentId = agentId,
            Action = action,
            Decision = decision,
        };

        try
        {
            var json = JsonSerializer.Serialize(record, SerializeOptions);
            var result = _chain.AppendAsync(json, CancellationToken.None).GetAwaiter().GetResult();
            if (result.IsSuccess)
            {
                // Only on a confirmed append — counting a failed write here would make a dashboard
                // built on this metric read as a healthy, growing audit trail while governance.jsonl
                // silently stopped receiving records.
                GovernanceMetrics.AuditEvents.Add(1,
                    new KeyValuePair<string, object?>(GovernanceConventions.Action, action));
            }
            else
            {
                GovernanceMetrics.AuditWriteFailures.Add(1,
                    new KeyValuePair<string, object?>(GovernanceConventions.Action, action));
                _logger.LogError(
                    "Failed to append governance audit record for agent {AgentId}, action {Action}: {Reason}",
                    agentId, action, string.Join("; ", result.Errors));
            }
        }
        catch (Exception ex)
        {
            // Catch-all, deliberately: HashChainedJsonlWriter.AppendAsync already catches IOException
            // and UnauthorizedAccessException internally and returns a failed Result rather than
            // throwing, so narrower typed catches here are unreachable dead code — what can actually
            // escape is e.g. ObjectDisposedException from a shutdown race on this writer's semaphore.
            // See this class's remarks: a governance audit write failure degrades the audit trail's
            // completeness, it must never fail the tool-call decision it is recording.
            GovernanceMetrics.AuditWriteFailures.Add(1,
                new KeyValuePair<string, object?>(GovernanceConventions.Action, action));
            _logger.LogError(ex,
                "Failed to append governance audit record for agent {AgentId}, action {Action}",
                agentId, action);
        }
    }

    /// <inheritdoc />
    public bool VerifyChainIntegrity()
    {
        try
        {
            return _chain.VerifyChainAsync(CancellationToken.None).GetAwaiter().GetResult().IsValid;
        }
        catch (Exception ex)
        {
            // Same exception-safety contract as Log (see this class's remarks): an inability to
            // verify is reported as "not verified," never as an unhandled exception.
            _logger.LogError(ex, "Failed to verify the governance audit chain at {FilePath}", _filePath);
            return false;
        }
    }

    /// <summary>
    /// Gets the total number of audit entries in the chain, computed by walking it end to end. No
    /// production caller reads this today (verified — see #407's implementation notes); the full-chain
    /// scan is acceptable only because of that.
    /// </summary>
    public int EntryCount
    {
        get
        {
            try
            {
                return (int)_chain.VerifyChainAsync(CancellationToken.None).GetAwaiter().GetResult().VerifiedCount;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to count entries in the governance audit chain at {FilePath}", _filePath);
                return 0;
            }
        }
    }

    /// <inheritdoc />
    public Task<AuditChainVerificationResult> VerifyChainAsync(CancellationToken cancellationToken) =>
        _chain.VerifyChainAsync(cancellationToken);

    /// <inheritdoc cref="IDisposable.Dispose" />
    public void Dispose() => _chain.Dispose();

    /// <summary>One governance decision record, serialized as a single hash-chained JSONL line.</summary>
    private sealed record GovernanceAuditRecord
    {
        public required DateTimeOffset Timestamp { get; init; }
        public required string AgentId { get; init; }
        public required string Action { get; init; }
        public required string Decision { get; init; }
    }
}
