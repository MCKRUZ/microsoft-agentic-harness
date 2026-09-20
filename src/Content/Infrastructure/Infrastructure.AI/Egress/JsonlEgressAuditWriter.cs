using System.Text.Json;
using System.Text.Json.Serialization;
using Application.AI.Common.Interfaces.Audit;
using Application.AI.Common.Interfaces.Egress;
using Domain.AI.Audit;
using Domain.AI.Egress;
using Domain.AI.Identity;
using Domain.Common;
using Domain.Common.Config;
using Infrastructure.AI.Audit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Egress;

/// <summary>
/// Append-only JSONL audit writer for egress decisions. Mirrors the shape
/// established by <c>JsonlChangeAuditWriter</c>: one line per record,
/// snake_case JSON, enums-as-strings. Records are linked into a tamper-evident
/// hash-chain via <see cref="HashChainedJsonlWriter"/> so a retroactively
/// altered or deleted egress decision is detectable.
/// </summary>
/// <remarks>
/// <para>
/// Captures every decision regardless of verdict so operators can answer "what
/// did this skill reach out to?" and "what was blocked?" with equal fidelity.
/// An audit limited to denies hides the silent expansion of a skill's outbound
/// surface area over time.
/// </para>
/// </remarks>
public sealed class JsonlEgressAuditWriter : IEgressAuditWriter, IVerifiableAuditChain, IDisposable
{
    /// <inheritdoc />
    public string AuditName => "egress";

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
    private readonly ILogger<JsonlEgressAuditWriter> _logger;

    /// <summary>Initializes a new <see cref="JsonlEgressAuditWriter"/>.</summary>
    public JsonlEgressAuditWriter(
        IOptionsMonitor<AppConfig> config,
        ILogger<JsonlEgressAuditWriter> logger)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);

        var dir = config.CurrentValue.AI.Egress.AuditStoragePath;
        _filePath = Path.Combine(dir, "egress.jsonl");
        _chain = new HashChainedJsonlWriter(_filePath, logger);
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task AppendAsync(
        EgressDecision decision,
        AgentIdentity identity,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(identity);

        var record = new EgressAuditRecord
        {
            Timestamp = decision.DecidedAt,
            Allowed = decision.Allowed,
            Target = decision.Target.ToString(),
            Host = decision.Target.Host,
            Scheme = decision.Target.Scheme,
            Port = decision.Target.Port,
            Reason = decision.Reason,
            MatchedAllowlistEntry = decision.MatchedAllowlistEntry,
            FinalIpAddress = decision.FinalIpAddress,
            AgentIdentity = new EgressAuditIdentity
            {
                Tenant = identity.TenantId,
                Agent = identity.Id,
                Kind = identity.Kind.ToString()
            }
        };

        var json = JsonSerializer.Serialize(record, SerializeOptions);
        var result = await _chain.AppendAsync(json, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            var reason = string.Join("; ", result.Errors);
            _logger.LogError(
                "Failed to append egress audit line for target {Host} ({Allowed}): {Reason}",
                decision.Target.Host,
                decision.Allowed,
                reason);
            throw new IOException(
                $"Failed to append egress audit record for target {decision.Target.Host}: {reason}");
        }
    }

    /// <inheritdoc />
    public Task<AuditChainVerificationResult> VerifyChainAsync(CancellationToken cancellationToken) =>
        _chain.VerifyChainAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<EgressAuditRecord>>> GetRecordsAsync(
        EgressAuditQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var records = new List<EgressAuditRecord>();

        try
        {
            await foreach (var payload in _chain.ReadAllPayloadsAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    var record = JsonSerializer.Deserialize<EgressAuditRecord>(payload, DeserializeOptions);
                    if (record is not null)
                        records.Add(record);
                }
                catch (JsonException)
                {
                    _logger.LogWarning("Skipped corrupted egress audit record in {FilePath}", _filePath);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Failed to read egress audit records from {FilePath}", _filePath);
            return Result<IReadOnlyList<EgressAuditRecord>>.Fail($"Failed to read audit records: {ex.Message}");
        }

        var filtered = records.AsEnumerable();
        if (query.Start.HasValue)
            filtered = filtered.Where(r => r.Timestamp >= query.Start.Value);
        if (query.End.HasValue)
            filtered = filtered.Where(r => r.Timestamp <= query.End.Value);
        if (query.Allowed.HasValue)
            filtered = filtered.Where(r => r.Allowed == query.Allowed.Value);
        if (!string.IsNullOrEmpty(query.Host))
            filtered = filtered.Where(r => r.Host == query.Host);

        var result = filtered.OrderBy(r => r.Timestamp).ToList();
        return Result<IReadOnlyList<EgressAuditRecord>>.Success(result.AsReadOnly());
    }

    /// <inheritdoc />
    public void Dispose() => _chain.Dispose();
}
