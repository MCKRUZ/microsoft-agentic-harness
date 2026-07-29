using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Persistence;

/// <summary>
/// Shared read/write guards for the JSON payload columns of the governance-state database.
/// </summary>
/// <remarks>
/// <para>
/// Both governance-state stores (escalations and change proposals) persist their aggregates as JSON
/// text and face the same two hazards: a stored payload that no longer deserializes, and a payload
/// large enough to be worth rejecting at the write. Keeping the guards here rather than once per
/// store means the exception set below is written once. That matters because the set is easy to get
/// wrong by omission — miss a type when adding a new payload shape and a row that should have been
/// quarantined instead throws out of the scan, which is precisely the failure the guard exists to
/// prevent.
/// </para>
/// <para>
/// Serialization options come from <see cref="GovernanceStateJson"/>, so reads and writes cannot
/// drift onto different converter sets.
/// </para>
/// </remarks>
public static class GovernanceStatePayload
{
    /// <summary>
    /// The floor applied to the configured payload cap. A cap below this is treated as
    /// misconfiguration rather than honoured, because a cap of (say) zero would reject every write
    /// and disable durable governance state entirely.
    /// </summary>
    public const int MinimumMaxPayloadBytes = 1024;

    /// <summary>
    /// Classifies an exception as "this stored payload is unreadable" rather than as an
    /// infrastructure fault.
    /// </summary>
    /// <remarks>
    /// A corrupt tick value, an unrecognised enum name, and a truncated document surface as
    /// different exception types; all of them mean one row is bad, and none of them should fail the
    /// scan that is reading past it.
    /// </remarks>
    /// <param name="exception">The exception thrown while reading a payload.</param>
    /// <returns>True when the exception indicates an unreadable payload.</returns>
    public static bool IsUnreadablePayload(Exception exception) =>
        exception is JsonException or ArgumentException or FormatException or OverflowException;

    /// <summary>
    /// Deserializes one stored payload, returning null and logging when it is unreadable so the
    /// caller can skip the row while leaving it on disk for manual inspection.
    /// </summary>
    /// <typeparam name="T">The payload type.</typeparam>
    /// <param name="json">The stored JSON.</param>
    /// <param name="logger">Logger for the failure line.</param>
    /// <param name="description">What the payload is, for the log line (for example "escalation outcome").</param>
    /// <param name="recordId">The owning record's id, for the log line.</param>
    /// <returns>The payload, or null when it is absent or unreadable.</returns>
    public static T? TryDeserialize<T>(string json, ILogger logger, string description, object recordId)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(logger);

        try
        {
            return JsonSerializer.Deserialize<T>(json, GovernanceStateJson.Options);
        }
        catch (Exception ex) when (IsUnreadablePayload(ex))
        {
            logger.LogError(ex,
                "Skipping unreadable persisted {Description} for governance record {RecordId}; " +
                "the row is preserved for manual inspection",
                description, recordId);
            return null;
        }
    }

    /// <summary>
    /// Serializes a payload and rejects it when it exceeds the configured cap, so an oversized
    /// document fails loudly at the write rather than being stored and failing every later read.
    /// </summary>
    /// <typeparam name="T">The payload type.</typeparam>
    /// <param name="value">The value to serialize.</param>
    /// <param name="configuredMaxBytes">
    /// The raw configured cap. The <see cref="MinimumMaxPayloadBytes"/> floor is applied here, so no
    /// caller has to remember to apply it.
    /// </param>
    /// <param name="subject">What is being written, for the failure message (a column or record).</param>
    /// <returns>The serialized JSON.</returns>
    /// <exception cref="InvalidOperationException">The payload exceeds the effective cap.</exception>
    public static string SerializeGuarded<T>(T value, int configuredMaxBytes, string subject)
    {
        var json = JsonSerializer.Serialize(value, GovernanceStateJson.Options);
        var maxBytes = Math.Max(MinimumMaxPayloadBytes, configuredMaxBytes);
        var byteCount = Encoding.UTF8.GetByteCount(json);

        if (byteCount > maxBytes)
        {
            throw new InvalidOperationException(
                $"Governance-state payload for {subject} is {byteCount} bytes, exceeding the configured " +
                $"maximum of {maxBytes}. Raise AppConfig:AI:Governance:DurableState:MaxPayloadBytes or " +
                "reduce the payload size.");
        }

        return json;
    }
}
