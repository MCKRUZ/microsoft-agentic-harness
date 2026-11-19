using System.Text.Json;
using Application.AI.Common.Interfaces.Audit;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Telemetry;
using Application.AI.Common.OpenTelemetry.Metrics;
using Domain.AI.Audit;
using Domain.AI.Governance;
using Domain.AI.Telemetry.Conventions;
using Domain.Common;
using Domain.Common.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Domain.Common.Compliance;

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
/// <see cref="HashChainedJsonlWriter.AppendAsync(string, CancellationToken)"/> (<c>GetAwaiter().GetResult()</c>) rather than
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
[PersonalDataStore(
    "Hash-chained JSONL under {Governance:AuditStoragePath} (default .agent-sessions/governance): agent id, action and decision, including approver names and reasons and memory-write keys. Kept by design: deleting records would break the chain. A record that could not be appended is held, as the same line, in {Governance:AuditStoragePath}/governance-pending until replay writes it to the chain, then deleted.",
    PersonalDataCategories.Identifiers | PersonalDataCategories.PeopleNames | PersonalDataCategories.DiagnosticText,
    PersonalDataKey.Unkeyed,
    RetentionBasis.KeptByDesign,
    ErasureSupport.Exempt,
    ExportSupport.NotSupported)]
public sealed class JsonlGovernanceAuditWriter : IGovernanceAuditService, IVerifiableAuditChain, IReplaysHeldAuditRecords, IDisposable
{
    private static readonly JsonSerializerOptions SerializeOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
    };

    private static readonly JsonSerializerOptions DeserializeOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _filePath;
    private readonly HashChainedJsonlWriter _chain;
    private readonly ILogger<JsonlGovernanceAuditWriter> _logger;
    private readonly IAuditPseudonymizer _pseudonymizer;
    private readonly IContentRedactionFilter _redaction;

    /// <summary>Initializes a new instance of <see cref="JsonlGovernanceAuditWriter"/>.</summary>
    /// <param name="config">Application configuration providing the audit storage path.</param>
    /// <param name="timeProvider">Decides which day-file a record is appended to (#416).</param>
    /// <param name="logger">Logger for operational diagnostics.</param>
    /// <param name="pseudonymizer">
    /// Records the caller behind a direct invocation (<c>direct-invoke:{user id}</c>) as a keyed reference (#903).
    /// </param>
    /// <param name="redaction">
    /// Redacts the action and decision text, which producers fill with free text: a delegation's model reasoning, a memory
    /// key, an approver's reason (#903).
    /// </param>
    public JsonlGovernanceAuditWriter(
        IOptionsMonitor<AppConfig> config,
        TimeProvider timeProvider,
        ILogger<JsonlGovernanceAuditWriter> logger,
        IAuditPseudonymizer pseudonymizer,
        IContentRedactionFilter redaction)
    {
        ArgumentNullException.ThrowIfNull(redaction);
        _redaction = redaction;
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(pseudonymizer);
        _pseudonymizer = pseudonymizer;

        var chain = AuditChains.Governance(config.CurrentValue, timeProvider);
        _filePath = chain.Segments.LegacyFile!;
        _chain = new HashChainedJsonlWriter(chain, logger, config.CurrentValue.AI.Audit.ArchivePath);
        _logger = logger;
    }

    /// <inheritdoc />
    public string AuditName => "governance";

    /// <inheritdoc />
    public string AuditLocation => _filePath;

    /// <inheritdoc />
    public string PendingLocation => _chain.PendingLocation!;

    /// <inheritdoc />
    public Task<PendingRecordsReplay> ReplayPendingAsync(CancellationToken cancellationToken) =>
        _chain.ReplayPendingAsync(cancellationToken);

    /// <inheritdoc />
    public void Log(string agentId, string action, string decision)
    {
        // Metrics and error logs name the action by its kind only ("memory_write", "delegate"): the full text carries
        // memory keys, agent ids and paths, which a metric label would publish raw and without bound (#903).
        var actionKind = ActionKind(action);
        try
        {
            var record = new GovernanceAuditRecord
            {
                Timestamp = DateTimeOffset.UtcNow,
                AgentId = RecordedAgentId(agentId ?? string.Empty),
                Action = RedactOrPlaceholder(action),
                Decision = RedactOrPlaceholder(decision),
            };

            var json = JsonSerializer.Serialize(record, SerializeOptions);
            var result = _chain.AppendAsync(json, CancellationToken.None).GetAwaiter().GetResult();
            if (result.IsSuccess)
            {
                // Only on a confirmed append — counting a failed write here would make a dashboard
                // built on this metric read as a healthy, growing audit trail while governance.jsonl
                // silently stopped receiving records.
                GovernanceMetrics.AuditEvents.Add(1,
                    new KeyValuePair<string, object?>(GovernanceConventions.Action, actionKind));
            }
            else
            {
                GovernanceMetrics.AuditWriteFailures.Add(1,
                    new KeyValuePair<string, object?>(GovernanceConventions.Action, actionKind));
                _logger.LogError(
                    "Failed to append governance audit record for action {Action}: {Reason}",
                    actionKind, string.Join("; ", result.Errors));
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
                new KeyValuePair<string, object?>(GovernanceConventions.Action, actionKind));
            _logger.LogError(ex, "Failed to append governance audit record for action {Action}", actionKind);
        }
    }

    // A redaction filter can time out or, when a deployment replaces it, fail outright. The record is still written,
    // without the text it could not clean: an audit write must never fail the decision it records, nor write raw text.
    private string RedactOrPlaceholder(string? text)
    {
        try
        {
            return AuditText.Redact(text ?? string.Empty, _redaction, _pseudonymizer);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not redact governance audit text; recording a placeholder instead");
            return RedactionFailed;
        }
    }

    private static string ActionKind(string? action) =>
        string.IsNullOrEmpty(action) ? "unknown" : action.Split(':', 2)[0];

    /// <summary>Recorded in place of text the redaction filter failed on.</summary>
    internal const string RedactionFailed = "[REDACTION_FAILED]";

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
    /// Gets the total number of audit entries in the chain: one past the sequence of its newest verified record,
    /// since sequences run from 0 without gaps. Archived records count (#416) although a verification pass reads
    /// only from the archive's newest record on, so this is not the pass's <c>VerifiedCount</c>. No production
    /// caller reads this today (verified — see #407's implementation notes); the verification pass it runs is
    /// acceptable only because of that.
    /// </summary>
    public int EntryCount
    {
        get
        {
            try
            {
                return (int)(_chain.VerifyChainAsync(CancellationToken.None).GetAwaiter().GetResult().HeadSequence + 1);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to count entries in the governance audit chain at {FilePath}", _filePath);
                return 0;
            }
        }
    }

    /// <inheritdoc />
    public Task<AuditChainVerificationResult> VerifyChainAsync(
        AuditChainCheckpoint? checkpoint, CancellationToken cancellationToken) =>
        _chain.VerifyChainAsync(checkpoint, cancellationToken);

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<GovernanceAuditRecord>>> GetRecordsAsync(
        GovernanceAuditQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var records = new List<GovernanceAuditRecord>();

        try
        {
            await foreach (var payload in _chain.ReadAllPayloadsAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    var record = JsonSerializer.Deserialize<GovernanceAuditRecord>(payload, DeserializeOptions);
                    if (record is not null)
                        records.Add(record);
                }
                catch (JsonException)
                {
                    _logger.LogWarning("Skipped corrupted governance audit record in {FilePath}", _filePath);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Failed to read governance audit records from {FilePath}", _filePath);
            return Result<IReadOnlyList<GovernanceAuditRecord>>.Fail($"Failed to read audit records: {ex.Message}");
        }

        var filtered = records.AsEnumerable();
        if (query.Start.HasValue)
            filtered = filtered.Where(r => r.Timestamp >= query.Start.Value);
        if (query.End.HasValue)
            filtered = filtered.Where(r => r.Timestamp <= query.End.Value);
        if (!string.IsNullOrEmpty(query.AgentId))
        {
            // A direct invocation's caller is recorded as a reference since #903, so look for both forms: the records
            // written before then hold the raw id, the ones since hold the reference.
            var recorded = RecordedAgentId(query.AgentId);
            filtered = filtered.Where(r => r.AgentId == query.AgentId || r.AgentId == recorded);
        }

        var result = filtered.OrderBy(r => r.Timestamp).ToList();
        return Result<IReadOnlyList<GovernanceAuditRecord>>.Success(result.AsReadOnly());
    }

    /// <inheritdoc cref="IDisposable.Dispose" />
    public void Dispose() => _chain.Dispose();

    // A direct invocation runs as "direct-invoke:{user id}"; the trail records the caller as a reference (#903).
    private string RecordedAgentId(string agentId) => AuditAgentIds.Recorded(agentId, _pseudonymizer);
}
