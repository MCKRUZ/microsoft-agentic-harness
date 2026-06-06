using System.Text.Json;
using System.Text.Json.Serialization;
using Application.AI.Common.Interfaces.Changes;
using Domain.AI.Changes;
using Domain.AI.Identity;
using Domain.Common.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Changes;

/// <summary>
/// Append-only JSONL audit writer for change-proposal gate decisions. Mirrors
/// the shape established by <c>JsonlEscalationAuditStore</c> and
/// <c>JsonlDriftAuditStore</c>: one line per record, snake_case JSON,
/// enums-as-strings, written under a <see cref="SemaphoreSlim"/> for thread
/// safety, file opened with <c>FileShare.ReadWrite</c> for concurrent reads.
/// </summary>
public sealed class JsonlChangeAuditWriter : IChangeAuditWriter, IDisposable
{
    private static readonly JsonSerializerOptions SerializeOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _filePath;
    private readonly ILogger<JsonlChangeAuditWriter> _logger;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    /// <summary>Initializes a new <see cref="JsonlChangeAuditWriter"/>.</summary>
    public JsonlChangeAuditWriter(
        IOptionsMonitor<AppConfig> config,
        ILogger<JsonlChangeAuditWriter> logger)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);

        var dir = config.CurrentValue.AI.Changes.AuditStoragePath;
        _filePath = Path.Combine(dir, "changes.jsonl");
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

        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);

        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var stream = new FileStream(
                _filePath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.ReadWrite);
            await using var writer = new StreamWriter(stream);
            var json = JsonSerializer.Serialize(record, SerializeOptions);
            await writer.WriteLineAsync(json).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to append ChangeProposal audit line for proposal {ProposalId} gate {GateKey}.",
                proposal.Id,
                decision.GateKey);
            throw;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _semaphore.Dispose();

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
