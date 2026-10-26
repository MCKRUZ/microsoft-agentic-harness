namespace Application.AI.Common.Interfaces.Telemetry;

/// <summary>
/// Replaces a value that can identify a person with a keyed hash before it leaves the process in telemetry or logs (#903):
/// a span attribute such as an email, or the fingerprint of a tool call's arguments. The same value always gives the same
/// result within a key, so counts and joins still work, but without the key the result cannot be reversed or confirmed by
/// hashing a guess, which an unkeyed hash of a low-entropy value (an email, a short argument list) allows.
/// </summary>
/// <remarks>
/// <para>
/// One key for every telemetry pseudonym, held for the life of the process: <c>Observability:PiiFiltering:HmacKey</c>
/// when configured, otherwise a random key made at startup. A random key keeps the pseudonyms private but changes them on
/// every restart and differs between instances, so per-person counts no longer add up across them; configure the key
/// wherever that matters.
/// </para>
/// <para>
/// Not for the erasure audit trail's subject references, which must stay stable for as long as the trail is kept and use
/// their own key (<c>Erasure:SubjectReferenceKey</c>), so rotating this one never breaks them.
/// </para>
/// </remarks>
public interface ITelemetryPseudonymizer
{
    /// <summary>The keyed hash of a span attribute's value, as lower-case hex.</summary>
    /// <param name="value">The value to replace.</param>
    /// <returns>HMAC-SHA256 of <paramref name="value"/> under the telemetry key.</returns>
    string HashAttribute(string value);

    /// <summary>
    /// The keyed fingerprint of <paramref name="value"/> for one named purpose, as lower-case hex. Each purpose hashes
    /// under its own key derived from the telemetry key, so a fingerprint for one purpose cannot be produced through an
    /// attribute hash or another purpose's fingerprint, whatever text is fed to them.
    /// </summary>
    /// <param name="purpose">What the fingerprint is for (<see cref="Purposes"/>).</param>
    /// <param name="value">The value to fingerprint.</param>
    /// <returns>
    /// HMAC-SHA256 of <paramref name="value"/> under the purpose's key: HKDF-Expand (SHA-256) of the telemetry key, with
    /// the purpose as the info.
    /// </returns>
    string Fingerprint(string purpose, string value);

    /// <summary>The purposes fingerprints are made for.</summary>
    public static class Purposes
    {
        /// <summary>The serialized arguments of a tool call, as recorded on its trace span.</summary>
        public const string ToolArguments = "tool-arguments";

        /// <summary>
        /// The raw request arguments of an MCP tool call, as logged on its audit line. A purpose of its own because the
        /// span records the arguments after the PII filter has run on them, so the two would not match even under one
        /// purpose; the audit line joins its trace through the correlation id instead.
        /// </summary>
        public const string McpAuditArguments = "mcp-audit-arguments";
    }
}
