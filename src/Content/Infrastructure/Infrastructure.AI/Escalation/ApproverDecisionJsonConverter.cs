using System.Text.Json;
using System.Text.Json.Serialization;
using Domain.AI.Escalation;
using Domain.Common.Helpers;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Escalation;

/// <summary>
/// Reads and writes <see cref="ApproverDecision"/> so a record written before
/// <see cref="ApproverVerdict"/> existed — carrying only a boolean <c>approved</c> property —
/// still deserializes correctly under every store that persists a decision.
/// </summary>
/// <remarks>
/// <para>
/// Delegates to a private <see cref="Wire"/> DTO rather than writing properties by hand, so the
/// caller's <see cref="JsonSerializerOptions.PropertyNamingPolicy"/> (snake_case for the JSONL
/// audit log, unset/PascalCase for the EF governance-state store) is applied automatically and
/// consistently — this converter contains no naming-policy logic of its own.
/// </para>
/// <para>
/// <b>Fail-closed on read.</b> A record with a <c>verdict</c> property uses it. A record with
/// neither is legacy: it uses <c>approved</c> (<see langword="true"/> → <see cref="ApproverVerdict.Approve"/>,
/// otherwise → <see cref="ApproverVerdict.Deny"/>). A <c>verdict</c> naming a value this build
/// does not recognize — a hand-edited row, or one written by a newer build — resolves to
/// <see cref="ApproverVerdict.Deny"/> with a log, never a thrown exception: an unrecognized
/// governance verdict must degrade to the safest reading, not abort the read.
/// </para>
/// <para>
/// <b>Writes only <c>verdict</c>.</b> No legacy <c>approved</c> mirror is emitted. A mirror would
/// put two sources of truth inside one governance payload — a corrupted single field could then
/// make two readers of the same sealed record disagree — and it would not restore rollback safety
/// anyway, since a rolled-back build has no <see cref="EscalationRequest.RevisionRound"/> either.
/// </para>
/// </remarks>
public sealed class ApproverDecisionJsonConverter : JsonConverter<ApproverDecision>
{
    private readonly ILogger? _logger;

    /// <summary>Initializes a new instance with no logger — an unrecognized verdict still fails closed, silently.</summary>
    public ApproverDecisionJsonConverter() : this(logger: null) { }

    /// <summary>Initializes a new instance that logs when a verdict fails closed for being unrecognized.</summary>
    public ApproverDecisionJsonConverter(ILogger? logger) => _logger = logger;

    /// <inheritdoc />
    public override ApproverDecision? Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var wire = JsonSerializer.Deserialize<Wire>(ref reader, options);
        if (wire is null)
            return null;

        if (wire.ApproverName is null)
            throw new JsonException("Approver decision payload is missing 'approver_name'.");

        if (wire.RespondedAt is null)
            throw new JsonException("Approver decision payload is missing 'responded_at'.");

        return new ApproverDecision
        {
            ApproverName = wire.ApproverName,
            Verdict = ResolveVerdict(wire),
            Reason = wire.Reason,
            Instructions = wire.Instructions,
            RespondedAt = wire.RespondedAt.Value
        };
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, ApproverDecision value, JsonSerializerOptions options)
    {
        var wire = new Wire
        {
            ApproverName = value.ApproverName,
            Verdict = value.Verdict.ToString(),
            Reason = value.Reason,
            Instructions = value.Instructions,
            RespondedAt = value.RespondedAt
        };

        JsonSerializer.Serialize(writer, wire, options);
    }

    /// <summary>
    /// Resolves the effective verdict: the modern <c>verdict</c> property when present and
    /// recognized, else the legacy <c>approved</c> boolean, else <see cref="ApproverVerdict.Deny"/>.
    /// </summary>
    private ApproverVerdict ResolveVerdict(Wire wire)
    {
        if (wire.Verdict is { } raw)
        {
            // EnumNameHelper.TryParseName, not a bare Enum.TryParse: the bare form accepts a
            // numeric string outside the defined range AND reads a comma-separated string as a
            // bitwise OR regardless of [Flags] — "Deny,Approve" (0|1) resolves to a value
            // Enum.IsDefined cannot tell apart from a clean "Approve". This repo has been bitten
            // by exactly this gap three times before (#296, #300, #312); EnumNameHelper is the
            // fix, and every other governance enum in the codebase already goes through it.
            if (EnumNameHelper.TryParseName<ApproverVerdict>(raw, out var parsed))
                return parsed;

            _logger?.LogWarning(
                "Approver decision carries an unrecognized verdict '{Verdict}' — treating as Deny (fail-closed).",
                raw);
            return ApproverVerdict.Deny;
        }

        return wire.Approved switch
        {
            true => ApproverVerdict.Approve,
            _ => ApproverVerdict.Deny
        };
    }

    /// <summary>
    /// The wire shape of an <see cref="ApproverDecision"/>. Every property is optional so a
    /// legacy or partially corrupted payload deserializes into this DTO without throwing —
    /// <see cref="Read"/> alone decides what is fatal.
    /// </summary>
    private sealed class Wire
    {
        public string? ApproverName { get; init; }

        /// <summary>The verdict's enum member name (e.g. "Approve"), or null on a legacy record.</summary>
        public string? Verdict { get; init; }

        /// <summary>The pre-<see cref="ApproverVerdict"/> boolean shape. Read-only fallback; never
        /// written — <see cref="JsonIgnoreCondition.WhenWritingNull"/> keeps a fresh write from
        /// emitting even a null "approved" property, so "verdict only" is true of the actual
        /// bytes on the wire, not just of the values this build populates.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? Approved { get; init; }

        public string? Reason { get; init; }

        public string? Instructions { get; init; }

        public DateTimeOffset? RespondedAt { get; init; }
    }
}
