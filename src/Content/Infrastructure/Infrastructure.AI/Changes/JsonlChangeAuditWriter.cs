using System.Text.Json;
using System.Text.Json.Serialization;
using Application.AI.Common.Interfaces.Audit;
using Application.AI.Common.Interfaces.Changes;
using Domain.AI.Audit;
using Domain.AI.Changes;
using Domain.AI.Identity;
using Domain.Common;
using Domain.Common.Config;
using Infrastructure.AI.Audit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Domain.Common.Compliance;
using Application.AI.Common.Interfaces.Telemetry;
using Domain.AI.Telemetry.Redaction;

namespace Infrastructure.AI.Changes;

/// <summary>
/// Append-only JSONL audit writer for change-proposal gate decisions. Mirrors
/// the shape established by <c>JsonlEscalationAuditStore</c> and
/// <c>JsonlDriftAuditStore</c>: one line per record, snake_case JSON,
/// enums-as-strings. Records are linked into a tamper-evident hash-chain via
/// <see cref="HashChainedJsonlWriter"/> so a retroactively altered or deleted
/// decision is detectable.
/// </summary>
[PersonalDataStore(
    "Hash-chained JSONL under {Changes:AuditStoragePath} (default .agent-sessions/changes) of change-gate decisions: proposal id, gate, decision, redacted reason, reviewer and direct-invocation caller as keyed references (#903; records written earlier hold them raw), agent and tenant. Kept by design: deleting records would break the chain.",
    PersonalDataCategories.Identifiers | PersonalDataCategories.DiagnosticText,
    PersonalDataKey.Tenant,
    RetentionBasis.KeptByDesign,
    ErasureSupport.Exempt,
    ExportSupport.NotSupported)]
public sealed class JsonlChangeAuditWriter : IChangeAuditWriter, IVerifiableAuditChain, IDisposable
{
    /// <inheritdoc />
    public string AuditName => "changes";

    /// <inheritdoc />
    public string AuditLocation => _filePath;

    private static readonly JsonSerializerOptions SerializeOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly JsonSerializerOptions DeserializeOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _filePath;
    private readonly HashChainedJsonlWriter _chain;
    private readonly ILogger<JsonlChangeAuditWriter> _logger;
    private readonly IAuditPseudonymizer _pseudonymizer;
    private readonly IContentRedactionFilter _redaction;

    /// <summary>Initializes a new <see cref="JsonlChangeAuditWriter"/>.</summary>
    /// <param name="config">Application configuration providing the audit storage path.</param>
    /// <param name="timeProvider">Decides which day-file a record is appended to (#416).</param>
    /// <param name="logger">Logger for operational diagnostics.</param>
    /// <param name="pseudonymizer">Records the reviewer and a direct invocation's caller as references (#903).</param>
    /// <param name="redaction">Redacts identifiers from the reviewer's reason.</param>
    public JsonlChangeAuditWriter(
        IOptionsMonitor<AppConfig> config,
        TimeProvider timeProvider,
        ILogger<JsonlChangeAuditWriter> logger,
        IAuditPseudonymizer pseudonymizer,
        IContentRedactionFilter redaction)
    {
        ArgumentNullException.ThrowIfNull(pseudonymizer);
        ArgumentNullException.ThrowIfNull(redaction);
        _pseudonymizer = pseudonymizer;
        _redaction = redaction;
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        var chain = AuditChains.Changes(config.CurrentValue, timeProvider);
        _filePath = chain.Segments.LegacyFile!;
        _chain = new HashChainedJsonlWriter(chain, logger, config.CurrentValue.AI.Audit.ArchivePath);
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task AppendAsync(
        ChangeProposal proposal,
        GateDecision decision,
        AgentIdentity identity,
        OrchestratorMode mode,
        string correlationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(identity);

        var record = new ChangeAuditRecord
        {
            Timestamp = decision.Timestamp,
            ProposalId = proposal.Id,
            GateKey = decision.GateKey,
            Decision = decision.Action,
            Reason = decision.Reason is null ? null : AuditText.Redact(decision.Reason, _redaction, _pseudonymizer.KeyId),
            EvidenceHash = decision.EvidenceHash,
            ReviewerId = RecordedReviewer(decision.ReviewerId),
            BlastRadius = proposal.BlastRadius,
            TargetKind = proposal.Target.Kind,
            Mode = mode.ToString(),
            CorrelationId = correlationId,
            AgentIdentity = new ChangeAuditIdentity
            {
                Tenant = identity.TenantId,
                Agent = AuditAgentIds.Recorded(identity.Id, _pseudonymizer),
                Kind = identity.Kind.ToString()
            },
            DurationMs = decision.DurationMs
        };

        var json = JsonSerializer.Serialize(record, SerializeOptions);
        var result = await _chain.AppendAsync(json, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            var reason = string.Join("; ", result.Errors);
            _logger.LogError(
                "Failed to append ChangeProposal audit line for proposal {ProposalId} gate {GateKey}: {Reason}",
                proposal.Id,
                decision.GateKey,
                reason);
            throw new IOException(
                $"Failed to append change audit record for proposal {proposal.Id}: {reason}");
        }
    }

    /// <inheritdoc />
    public Task<AuditChainVerificationResult> VerifyChainAsync(
        AuditChainCheckpoint? checkpoint, CancellationToken cancellationToken) =>
        _chain.VerifyChainAsync(checkpoint, cancellationToken);

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<ChangeAuditRecord>>> GetRecordsAsync(
        ChangeAuditQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var records = new List<ChangeAuditRecord>();

        try
        {
            await foreach (var payload in _chain.ReadAllPayloadsAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    var record = JsonSerializer.Deserialize<ChangeAuditRecord>(payload, DeserializeOptions);
                    if (record is not null)
                        records.Add(record);
                }
                catch (JsonException)
                {
                    _logger.LogWarning("Skipped corrupted change audit record in {FilePath}", _filePath);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Failed to read change audit records from {FilePath}", _filePath);
            return Result<IReadOnlyList<ChangeAuditRecord>>.Fail($"Failed to read audit records: {ex.Message}");
        }

        var filtered = records.AsEnumerable();
        if (query.Start.HasValue)
            filtered = filtered.Where(r => r.Timestamp >= query.Start.Value);
        if (query.End.HasValue)
            filtered = filtered.Where(r => r.Timestamp <= query.End.Value);
        if (!string.IsNullOrEmpty(query.ProposalId))
            filtered = filtered.Where(r => r.ProposalId == query.ProposalId);
        if (!string.IsNullOrEmpty(query.GateKey))
            filtered = filtered.Where(r => r.GateKey == query.GateKey);
        if (query.Decision.HasValue)
            filtered = filtered.Where(r => r.Decision == query.Decision.Value);
        if (!string.IsNullOrEmpty(query.CorrelationId))
            filtered = filtered.Where(r => r.CorrelationId == query.CorrelationId);

        var result = filtered.OrderBy(r => r.Timestamp).ToList();
        return Result<IReadOnlyList<ChangeAuditRecord>>.Success(result.AsReadOnly());
    }

    /// <inheritdoc />
    public void Dispose() => _chain.Dispose();

    // The orchestrator's own auto-approval is recorded under a fixed system id, not a person; it stays readable.
    private string? RecordedReviewer(string? reviewerId) =>
        reviewerId is null || reviewerId == AutoApproverReviewerId ? reviewerId : _pseudonymizer.Person(reviewerId);

    /// <summary>The reviewer id the change orchestrator records for a proposal it approves itself.</summary>
    internal const string AutoApproverReviewerId = "auto-approver";
}
