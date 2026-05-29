using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Domain.Common.Config.Observability;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTelemetry;

namespace Infrastructure.Observability.Processors;

/// <summary>
/// Removes or hashes PII attributes from spans before they reach export backends.
/// Acts as a policy enforcement layer between instrumentation and storage.
/// </summary>
/// <remarks>
/// <para>
/// Two scrubbing actions are supported:
/// </para>
/// <list type="bullet">
///   <item><description>
///     <strong>Delete</strong> — attribute is removed entirely.
///     Use for values with no analytical value (auth headers, cookies).
///   </description></item>
///   <item><description>
///     <strong>Hash</strong> — attribute value is replaced with its SHA-256 hex digest.
///     Preserves cardinality for grouping/counting without exposing the raw PII
///     (e.g., "user@example.com" becomes "b4c9a289...").
///   </description></item>
/// </list>
/// <para>
/// Configured via <see cref="PiiFilteringConfig"/> in
/// <c>AppConfig.Observability.PiiFiltering</c>.
/// </para>
/// </remarks>
public sealed class PiiFilteringProcessor : BaseProcessor<Activity>
{
    private readonly ILogger<PiiFilteringProcessor> _logger;
    private readonly PiiFilteringConfig _config;
    private readonly HashSet<string> _deleteAttributes;
    private readonly HashSet<string> _hashAttributes;
    private readonly byte[]? _hmacKey;

    /// <summary>
    /// Initializes a new instance of the <see cref="PiiFilteringProcessor"/> class.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="appConfig">The application configuration containing PII filtering settings.</param>
    public PiiFilteringProcessor(
        ILogger<PiiFilteringProcessor> logger,
        IOptions<Domain.Common.Config.AppConfig> appConfig)
    {
        _logger = logger;
        _config = appConfig.Value.Observability.PiiFiltering;

        _deleteAttributes = new HashSet<string>(_config.DeleteAttributes, StringComparer.OrdinalIgnoreCase);
        _hashAttributes = new HashSet<string>(_config.HashAttributes, StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrEmpty(_config.HmacKey))
        {
            _hmacKey = Convert.FromBase64String(_config.HmacKey);
        }
        else if (_hashAttributes.Count > 0)
        {
            _logger.LogWarning(
                "PII filtering has {HashCount} hash attributes but no HmacKey — " +
                "using plain SHA-256. Set Observability:PiiFiltering:HmacKey for production",
                _hashAttributes.Count);
        }

        _logger.LogInformation(
            "PII filtering initialized: {DeleteCount} delete, {HashCount} hash, HMAC={HmacEnabled}",
            _deleteAttributes.Count,
            _hashAttributes.Count,
            _hmacKey is not null);
    }

    /// <inheritdoc />
    public override void OnEnd(Activity data)
    {
        if (!_config.Enabled)
            return;

        // Two-pass: scan then mutate (cannot modify TagObjects during enumeration).
        // Typical spans have few PII matches, so a small inline array avoids allocation.
        (string Key, bool IsHash, string Value)[]? matches = null;
        var matchCount = 0;

        foreach (var tag in data.TagObjects)
        {
            var isDelete = _deleteAttributes.Contains(tag.Key);
            var isHash = !isDelete && _hashAttributes.Contains(tag.Key) && tag.Value is string;

            if (!isDelete && !isHash)
                continue;

            matches ??= new (string, bool, string)[4];
            if (matchCount >= matches.Length)
                Array.Resize(ref matches, matches.Length * 2);

            matches[matchCount++] = (tag.Key, isHash, isHash ? (string)tag.Value! : string.Empty);
        }

        for (var i = 0; i < matchCount; i++)
        {
            var (key, isHash, value) = matches![i];
            data.SetTag(key, isHash ? HashValue(value) : null);
        }
    }

    private string HashValue(string value)
    {
        var input = Encoding.UTF8.GetBytes(value);
        var bytes = _hmacKey is not null
            ? HMACSHA256.HashData(_hmacKey, input)
            : SHA256.HashData(input);
        return Convert.ToHexStringLower(bytes);
    }
}
