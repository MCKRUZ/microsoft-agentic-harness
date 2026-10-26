namespace Domain.Common.Config.Observability;

/// <summary>
/// Configuration for PII filtering in the telemetry pipeline.
/// Removes or hashes sensitive span attributes before they reach export backends.
/// </summary>
/// <remarks>
/// <para>
/// Two actions are supported:
/// <list type="bullet">
///   <item><description><strong>Delete</strong> — attribute is removed entirely (e.g., auth headers)</description></item>
///   <item><description><strong>Hash</strong> — attribute value is replaced with an HMAC-SHA256 hash under
///   <see cref="HmacKey"/>, preserving cardinality for analytics without exposing the raw value (e.g., emails)</description></item>
/// </list>
/// </para>
/// </remarks>
public class PiiFilteringConfig
{
    /// <summary>
    /// Gets or sets whether PII filtering is enabled.
    /// When disabled, all attributes pass through unmodified.
    /// </summary>
    /// <value>Default: true.</value>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the attribute keys to delete entirely from spans.
    /// </summary>
    /// <value>
    /// Default: authorization header, cookies, API keys, set-cookie response header.
    /// </value>
    public List<string> DeleteAttributes { get; set; } =
    [
        "http.request.header.authorization",
        "http.request.header.cookie",
        "http.request.header.x-api-key",
        "http.response.header.set-cookie"
    ];

    /// <summary>
    /// Gets or sets the attribute keys whose values are replaced with a keyed hash.
    /// Preserves cardinality for analytics without exposing PII.
    /// Always HMAC-SHA256: under <see cref="HmacKey"/> when configured, otherwise under a random key made at startup.
    /// </summary>
    /// <value>Default: user email, end-user ID.</value>
    public List<string> HashAttributes { get; set; } =
    [
        "user.email",
        "enduser.id"
    ];

    /// <summary>
    /// Gets or sets the base64-encoded key for every telemetry pseudonym: the hashed span attributes, the
    /// <c>tool.input_hash</c> fingerprint and the MCP tool-call log line. Keyed so a reader of the traces or logs
    /// cannot confirm a low-entropy value (an email, a short argument list) by hashing a guess. At least 32 bytes;
    /// a set but malformed key refuses startup. Generate with:
    /// <c>Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))</c>.
    /// </summary>
    /// <value>
    /// Default: null — a random key is made at startup with a warning, so pseudonyms stay private but change on
    /// restart and differ between instances. Configure it where per-person counts must add up across them.
    /// </value>
    public string? HmacKey { get; set; }
}
