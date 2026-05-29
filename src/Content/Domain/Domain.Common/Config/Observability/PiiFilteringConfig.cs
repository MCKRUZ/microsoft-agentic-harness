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
///   <item><description><strong>Hash</strong> — attribute value is replaced with a SHA-256 hash,
///   preserving cardinality for analytics without exposing the raw value (e.g., emails)</description></item>
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
    /// Uses HMAC-SHA256 when <see cref="HmacKey"/> is configured, plain SHA-256 otherwise.
    /// </summary>
    /// <value>Default: user email, end-user ID.</value>
    public List<string> HashAttributes { get; set; } =
    [
        "user.email",
        "enduser.id"
    ];

    /// <summary>
    /// Gets or sets the base64-encoded HMAC key for hashing PII attribute values.
    /// When configured, HMAC-SHA256 replaces plain SHA-256 to prevent rainbow table
    /// attacks on low-entropy values. Generate with:
    /// <c>Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))</c>.
    /// </summary>
    /// <value>Default: null (falls back to plain SHA-256 with a startup warning).</value>
    public string? HmacKey { get; set; }
}
