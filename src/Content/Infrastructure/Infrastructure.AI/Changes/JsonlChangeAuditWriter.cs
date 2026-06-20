using System.Text.Json;
using System.Text.Json.Serialization;
using Application.AI.Common.Interfaces.Audit;
using Application.AI.Common.Interfaces.Changes;
using Domain.AI.Audit;
using Domain.AI.Changes;
using Domain.AI.Identity;
using Domain.Common.Config;
using Infrastructure.AI.Audit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Changes;

/// <summary>
/// Append-only JSONL audit writer for change-proposal gate decisions. Mirrors
/// the shape established by <c>JsonlEscalationAuditStore</c> and
/// <c>JsonlDriftAuditStore</c>: one line per record, snake_case JSON,
/// enums-as-strings. Records are linked into a tamper-evident hash-chain via
/// <see cref="HashChainedJsonlWriter"/> so a retroactively altered or deleted
/// decision is detectable.
/// </summary>
public sealed class JsonlChangeAuditWriter : IChangeAuditWriter, IVerifiableAuditChain, IDisposable
{
    /// <inheritdoc />
    public string AuditName => "changes";

    private static readonly JsonSerializerOptions SerializeOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly HashChainedJsonlWriter _chain;
    private readonly ILogger<JsonlChangeAuditWriter> _logger;

    /// <summary>Initializes a new <see cref="JsonlChangeAuditWriter"/>.</summary>
    public JsonlChangeAuditWriter(
        IOptionsMonitor<AppConfig> config,
        ILogger<JsonlChangeAuditWriter> logger)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);

        var dir = config.CurrentValue.AI.Changes.AuditStoragePath;
        _chain = new HashChainedJsonlWriter(Path.Combine(dir, "changes.jsonl"), logger);
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
            Reason = decision.Reason,
            EvidenceHash = decision.EvidenceHash,
            ReviewerId = decision.ReviewerId,
            BlastRadius = proposal.BlastRadius,
            TargetKind = proposal.Target.Kind,
            Mode = mode,
            CorrelationId = correlationId,
            AgentIdentity = new ChangeAuditIdentity
            {
                Tenant = identity.TenantId,
                Agent = identity.Id,
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
    public Task<AuditChainVerificationResult> VerifyChainAsync(CancellationToken cancellationToken) =>
        _chain.VerifyChainAsync(cancellationToken);

    /// <inheritdoc />
    public void Dispose() => _chain.Dispose();

    private sealed record ChangeAuditRecord
    {
        public required DateTimeOffset Timestamp { get; init; }
        public required string ProposalId { get; init; }
        public required string GateKey { get; init; }
        public required GateAction Decision { get; init; }
        public string Reason { get; init; } = string.Empty;
        public string? EvidenceHash { get; init; }
        public string? ReviewerId { get; init; }
        public required BlastRadius BlastRadius { get; init; }
        public required ChangeTargetKind TargetKind { get; init; }
        public required OrchestratorMode Mode { get; init; }
        public required string CorrelationId { get; init; }
        public required ChangeAuditIdentity AgentIdentity { get; init; }
        public required long DurationMs { get; init; }
    }

    private sealed record ChangeAuditIdentity
    {
        public string? Tenant { get; init; }
        public required string Agent { get; init; }
        public required string Kind { get; init; }
    }
}
