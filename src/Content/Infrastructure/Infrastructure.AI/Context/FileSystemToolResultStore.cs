using Application.AI.Common.Interfaces.Context;
using Domain.AI.Context;
using Domain.Common.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Context;

/// <summary>
/// Persists large tool results to disk and serves truncated previews for in-context use.
/// Small results (below <see cref="Domain.Common.Config.AI.ContextManagement.ToolResultStorageConfig.PerResultCharLimit"/>)
/// are returned inline without any disk I/O.
/// </summary>
public sealed class FileSystemToolResultStore : IToolResultStore
{
    private readonly IOptionsMonitor<AppConfig> _options;
    private readonly ILogger<FileSystemToolResultStore> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="FileSystemToolResultStore"/> class.
    /// </summary>
    /// <param name="options">Application configuration for storage thresholds and paths.</param>
    /// <param name="logger">Logger for storage diagnostics.</param>
    public FileSystemToolResultStore(
        IOptionsMonitor<AppConfig> options,
        ILogger<FileSystemToolResultStore> logger)
    {
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<ToolResultReference> StoreIfLargeAsync(
        string sessionId,
        string toolName,
        string? operation,
        string fullOutput,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        ArgumentNullException.ThrowIfNull(fullOutput);

        // H-1: sessionId must be a single safe path segment. Path.GetFileName equality
        // alone is insufficient — it lets "." and ".." through (GetFileName(("..")) == "..")
        // which Path.Combine then resolves to a parent directory, escaping the storage root.
        var safeSessionId = SanitizeSessionSegment(sessionId, nameof(sessionId));

        var config = _options.CurrentValue.AI.ContextManagement.ToolResultStorage;
        var resultId = Guid.NewGuid().ToString("N");
        var timestamp = DateTimeOffset.UtcNow;

        if (fullOutput.Length <= config.PerResultCharLimit)
        {
            _logger.LogDebug(
                "Tool result {ResultId} from {ToolName} is {Length} chars — keeping inline",
                resultId, toolName, fullOutput.Length);

            return new ToolResultReference
            {
                ResultId = resultId,
                ToolName = toolName,
                Operation = operation,
                PreviewContent = fullOutput,
                FullContentPath = null,
                SizeChars = fullOutput.Length,
                Timestamp = timestamp
            };
        }

        var storagePath = Path.Combine(config.StoragePath, safeSessionId, "tool-results", $"{resultId}.json");
        var directory = Path.GetDirectoryName(storagePath)!;
        Directory.CreateDirectory(directory);

        await File.WriteAllTextAsync(storagePath, fullOutput, cancellationToken);

        var previewLength = Math.Min(config.PreviewSizeChars, fullOutput.Length);
        var preview = $"{fullOutput[..previewLength]}\n... [{fullOutput.Length} chars persisted to {resultId}]";

        _logger.LogInformation(
            "Tool result {ResultId} from {ToolName} persisted to disk: {Length} chars at {Path}",
            resultId, toolName, fullOutput.Length, storagePath);

        return new ToolResultReference
        {
            ResultId = resultId,
            ToolName = toolName,
            Operation = operation,
            PreviewContent = preview,
            FullContentPath = storagePath,
            SizeChars = fullOutput.Length,
            Timestamp = timestamp
        };
    }

    /// <inheritdoc />
    public async Task<string> RetrieveFullContentAsync(
        string resultId,
        string scopeId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resultId);
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeId);

        // resultId is MODEL-SUPPLIED (ToolResultFetchTool passes the LLM's own 'resultId' argument
        // straight through) and is interpolated into a path below, so it is sanitized here before it
        // can become one. Sanitizing scopeId alone is not enough: Path.Combine performs no
        // normalization, so "../../<other-scope>/tool-results/<id>" lands in ANOTHER scope's
        // directory once the file APIs normalize it, and a ROOTED resultId ("C:/…", "\\\\host\\share\\…")
        // makes Path.Combine discard every earlier segment outright — arbitrary *.json read, including
        // appsettings.json and user-secrets' secrets.json, plus UNC egress on Windows.
        //
        // Validated by SHAPE rather than by stripping separators: StoreIfLargeAsync mints ids as, and
        // only as, Guid.NewGuid().ToString("N"), so accepting exactly that alphabet is the complete
        // enumeration of what can legitimately be asked for — a rejection here can never refuse a real
        // id. A malformed id is refused as KeyNotFoundException, with the same message shape as a
        // genuine miss, for the same reason a wrong scope is: nothing about the outcome may tell a
        // caller which of its guesses was better-formed than another.
        if (!Guid.TryParseExact(resultId, "N", out _))
        {
            throw new KeyNotFoundException($"No stored result found for id '{resultId}'.");
        }

        // Reconstructed deterministically from (scopeId, resultId) — the exact shape StoreIfLargeAsync
        // writes to — rather than trusted from _resultPaths, for two reasons at once (#521):
        //   1. Ownership: a caller supplying a DIFFERENT scopeId than the one this result was stored
        //      under can never reach it, because the reconstructed path simply lands in that OTHER
        //      caller's own directory, which does not contain this resultId's file. No separate
        //      "does scopeId match what I recorded" check is needed or possible to skip.
        //   2. Durability: _resultPaths is in-memory only and empties on every process restart, even
        //      though the file itself is still on disk — reconstructing the path means a restart no
        //      longer makes an already-spilled result unrecoverable.
        // SanitizeSessionSegment applies to scopeId for the identical path-traversal reason it already
        // applies to StoreIfLargeAsync's sessionId — this is the same path segment, read back.
        // Residual gap accepted (found in /simplify's review): reconstructing from CurrentValue rather
        // than a path captured at write time means a same-process hot reload of StoragePath between a
        // spill and its retrieval would point this read at a different root than the write used. Not
        // fixed — StoragePath is not a value anyone realistically hot-reloads mid-process.
        var safeScopeId = SanitizeSessionSegment(scopeId, nameof(scopeId));
        var config = _options.CurrentValue.AI.ContextManagement.ToolResultStorage;
        var storagePath = Path.Combine(config.StoragePath, safeScopeId, "tool-results", $"{resultId}.json");

        _logger.LogDebug("Retrieving full content for result {ResultId} from {Path}", resultId, storagePath);

        try
        {
            return await File.ReadAllTextAsync(storagePath, cancellationToken);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // One filesystem round-trip instead of File.Exists + ReadAllTextAsync (a /simplify
            // efficiency finding) — this also closes the check-then-read race the two-call version had
            // (the file could vanish between the check and the read). Deliberately the same exception,
            // with the same message shape, whether resultId was never stored at all or was stored under
            // a DIFFERENT scope — see the interface's own remarks on why "exists but not yours" must
            // read identically to "does not exist".
            throw new KeyNotFoundException($"No stored result found for id '{resultId}'.");
        }
    }

    /// <summary>
    /// Reduces <paramref name="sessionId"/> to a single safe path segment for use in
    /// <see cref="Path.Combine(string, string)"/>, rejecting any value that could escape
    /// the storage root via path traversal.
    /// </summary>
    /// <param name="sessionId">The caller-supplied session identifier.</param>
    /// <param name="paramName">
    /// The CALLING method's own parameter name to report in a thrown <see cref="ArgumentException"/> —
    /// this helper is shared by <see cref="StoreIfLargeAsync"/> (parameter <c>sessionId</c>) and
    /// <see cref="RetrieveFullContentAsync"/> (parameter <c>scopeId</c>); a hardcoded <c>nameof(sessionId)</c>
    /// would misreport the latter (a /code-review finding).
    /// </param>
    /// <returns>The validated session identifier, guaranteed to be a single path segment.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="sessionId"/> contains path separators, is rooted, is a relative
    /// directory reference ("." or ".."), or has trailing dots/spaces Windows would silently strip.
    /// </exception>
    private static string SanitizeSessionSegment(string sessionId, string paramName)
    {
        // Reject any path separator from EITHER platform, a rooted path, or a relative
        // directory reference. Path.GetFileName / Path.IsPathRooted are OS-specific — on
        // Linux '\' is an ordinary character, so a Windows-style "..\escape" slips through
        // GetFileName unchanged. Check both separators explicitly so the guard behaves
        // identically on every platform.
        if (sessionId.Contains('/') || sessionId.Contains('\\') || Path.IsPathRooted(sessionId))
        {
            throw new ArgumentException(
                "Session ID must be a single path segment without separators.", paramName);
        }

        // "." and ".." resolve to the storage root or a parent when combined, so reject them.
        if (sessionId is "." or "..")
        {
            throw new ArgumentException(
                "Session ID must not be a relative directory reference.", paramName);
        }

        // Windows silently trims trailing dots/spaces off a path segment, so "<id>" and "<id> " (or
        // "<id>.") resolve to the SAME directory there even though they compare unequal as strings —
        // two different scopes could collide onto one storage directory (a security-review finding).
        // Comparing against the OS-trimmed form works identically, and harmlessly, on every platform.
        if (sessionId != sessionId.TrimEnd(' ', '.'))
        {
            throw new ArgumentException(
                "Session ID must not have trailing dots or spaces.", paramName);
        }

        return sessionId;
    }
}
