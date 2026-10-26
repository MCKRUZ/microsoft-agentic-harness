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
using Domain.Common.Compliance;

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
[PersonalDataStore(
    "Hash-chained JSONL under {Egress:AuditStoragePath} (default .agent-sessions/egress) of every outbound request decision: target scheme, host and port with a keyed fingerprint of the full address (#903; records written earlier hold the full URI with query string), resolved IP, reason, agent and tenant. Kept by design: deleting records would break the chain.",
    PersonalDataCategories.NetworkTargets | PersonalDataCategories.Identifiers | PersonalDataCategories.DiagnosticText,
    PersonalDataKey.Tenant,
    RetentionBasis.KeptByDesign,
    ErasureSupport.Exempt,
    ExportSupport.NotSupported)]
public sealed class JsonlEgressAuditWriter : IEgressAuditWriter, IVerifiableAuditChain, IDisposable
{
    /// <inheritdoc />
    public string AuditName => "egress";

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
    private readonly ILogger<JsonlEgressAuditWriter> _logger;
    private readonly IAuditPseudonymizer _pseudonymizer;

    /// <summary>Initializes a new <see cref="JsonlEgressAuditWriter"/>.</summary>
    /// <param name="config">Application configuration providing the audit storage path.</param>
    /// <param name="timeProvider">Decides which day-file a record is appended to (#416).</param>
    /// <param name="logger">Logger for operational diagnostics.</param>
    /// <param name="pseudonymizer">
    /// Fingerprints the full target address and records a direct invocation's caller as a reference (#903).
    /// </param>
    public JsonlEgressAuditWriter(
        IOptionsMonitor<AppConfig> config,
        TimeProvider timeProvider,
        ILogger<JsonlEgressAuditWriter> logger,
        IAuditPseudonymizer pseudonymizer)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(pseudonymizer);
        _pseudonymizer = pseudonymizer;

        var chain = AuditChains.Egress(config.CurrentValue, timeProvider);
        _filePath = chain.Segments.LegacyFile!;
        _chain = new HashChainedJsonlWriter(chain, logger, config.CurrentValue.AI.Audit.ArchivePath);
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
            Target = RecordedTarget(decision.Target),
            Host = decision.Target.IsAbsoluteUri ? decision.Target.Host : string.Empty,
            Scheme = decision.Target.IsAbsoluteUri ? decision.Target.Scheme : string.Empty,
            Port = decision.Target.IsAbsoluteUri ? decision.Target.Port : 0,
            Reason = decision.Reason,
            MatchedAllowlistEntry = decision.MatchedAllowlistEntry,
            FinalIpAddress = decision.FinalIpAddress,
            AgentIdentity = new EgressAuditIdentity
            {
                Tenant = identity.TenantId,
                Agent = AuditAgentIds.Recorded(identity.Id, _pseudonymizer),
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
    public Task<AuditChainVerificationResult> VerifyChainAsync(
        AuditChainCheckpoint? checkpoint, CancellationToken cancellationToken) =>
        _chain.VerifyChainAsync(checkpoint, cancellationToken);

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

    // The trail is kept for good, and a path or query names people often enough ("/users/octocat", "?email=..."), so it
    // keeps where the request went (scheme, host, port; never user info) and a keyed fingerprint of the address the
    // request was sent to, which the key holder can match against a known one (#903). The fingerprint covers what goes on
    // the wire: scheme, host, port, path and query, in their escaped form, never the user info or the fragment, so it
    // cannot be used to confirm a guessed password. Match against new Uri(candidate) formatted the same way.
    // A relative target names no host to keep; it is recorded by its fingerprint alone rather than failing the write.
    private string RecordedTarget(Uri target) => target.IsAbsoluteUri
        ? target.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped) + "/#"
            + _pseudonymizer.Fingerprint(
                IAuditPseudonymizer.Purposes.EgressTarget,
                target.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped))
        : "#" + _pseudonymizer.Fingerprint(IAuditPseudonymizer.Purposes.EgressTarget, target.OriginalString);
}
