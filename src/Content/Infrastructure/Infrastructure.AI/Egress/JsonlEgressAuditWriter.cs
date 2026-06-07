using System.Text.Json;
using System.Text.Json.Serialization;
using Application.AI.Common.Interfaces.Egress;
using Domain.AI.Egress;
using Domain.AI.Identity;
using Domain.Common.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Egress;

/// <summary>
/// Append-only JSONL audit writer for egress decisions. Mirrors the shape
/// established by <c>JsonlChangeAuditWriter</c>: one line per record,
/// snake_case JSON, enums-as-strings, written under a <see cref="SemaphoreSlim"/>
/// for thread safety, file opened with <c>FileShare.ReadWrite</c> for
/// concurrent reads.
/// </summary>
/// <remarks>
/// <para>
/// Captures every decision regardless of verdict so operators can answer "what
/// did this skill reach out to?" and "what was blocked?" with equal fidelity.
/// An audit limited to denies hides the silent expansion of a skill's outbound
/// surface area over time.
/// </para>
/// </remarks>
public sealed class JsonlEgressAuditWriter : IEgressAuditWriter, IDisposable
{
    private static readonly JsonSerializerOptions SerializeOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _filePath;
    private readonly ILogger<JsonlEgressAuditWriter> _logger;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    /// <summary>Initializes a new <see cref="JsonlEgressAuditWriter"/>.</summary>
    public JsonlEgressAuditWriter(
        IOptionsMonitor<AppConfig> config,
        ILogger<JsonlEgressAuditWriter> logger)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);

        var dir = config.CurrentValue.AI.Egress.AuditStoragePath;
        _filePath = Path.Combine(dir, "egress.jsonl");
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
                "Failed to append egress audit line for target {Host} ({Allowed}).",
                decision.Target.Host,
                decision.Allowed);
            throw;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _semaphore.Dispose();

    private sealed record EgressAuditRecord
    {
        public required DateTimeOffset Timestamp { get; init; }
        public required bool Allowed { get; init; }
        public required string Target { get; init; }
        public required string Host { get; init; }
        public required string Scheme { get; init; }
        public required int Port { get; init; }
        public required string Reason { get; init; }
        public string? MatchedAllowlistEntry { get; init; }
        public string? FinalIpAddress { get; init; }
        public required EgressAuditIdentity AgentIdentity { get; init; }
    }

    private sealed record EgressAuditIdentity
    {
        public string? Tenant { get; init; }
        public required string Agent { get; init; }
        public required string Kind { get; init; }
    }
}
