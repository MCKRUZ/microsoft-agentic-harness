using System.Text.RegularExpressions;
using Application.AI.Common.Interfaces.Context;
using Application.AI.Common.Services.Governance;
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
    // Positive allowlist (#560) rather than an ever-growing set of rejected shapes: this is the
    // complete enumeration of characters a legitimate scope id can ever need (durable conversation
    // ids, plan/run ids, and minted GUIDs all fit it), so anything outside it is refused outright
    // rather than pattern-matched against known-dangerous cases. Neither '/' nor '\' — the only two
    // path separators across platforms — are in the set, which is what actually closes the
    // traversal/UNC-egress class this guards against; a rooted absolute path also cannot match
    // (no leading '/' , and a bare drive letter like "C:" is drive-relative, not rooted, and is not
    // a valid Windows path segment either way). The 128-char cap bounds an otherwise-unbounded
    // caller-supplied string before it becomes a directory name.
    private static readonly Regex AllowedSegmentCharset =
        new("^[A-Za-z0-9_.:-]{1,128}$", RegexOptions.Compiled);

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
        int? sizeThreshold = null,
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
        var effectiveThreshold = sizeThreshold ?? config.PerResultCharLimit;

        if (fullOutput.Length <= effectiveThreshold)
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

        // #563: bound what actually reaches disk to MaxSpillChars, no matter how large the tool's true
        // output was — an unbounded write is the exact "no silent caps" gap this cap exists to close,
        // just at the disk boundary instead of the context-window one. BoundedText.Cap with an empty
        // marker reuses its surrogate-pair-safe cut rather than a hand-rolled Substring, and is a no-op
        // whenever fullOutput is already within the cap. Enforced here, in the store, rather than by
        // every caller of StoreIfLargeAsync — the same "check a caller could forget is not a check"
        // reasoning SanitizeSessionSegment's own remarks already apply to scope enforcement.
        var (spillable, spillTruncated) = BoundedText.Cap(fullOutput, config.MaxSpillChars, marker: "");
        if (spillTruncated)
        {
            _logger.LogWarning(
                "Tool result {ResultId} from {ToolName} is {Length} chars, exceeding MaxSpillChars "
                + "({MaxSpillChars}) — only the first {MaxSpillChars} chars are persisted and "
                + "retrievable; the rest is unrecoverable",
                resultId, toolName, fullOutput.Length, config.MaxSpillChars, config.MaxSpillChars);
        }

        var storagePath = Path.Combine(config.StoragePath, safeSessionId, "tool-results", $"{resultId}.json");
        var directory = Path.GetDirectoryName(storagePath)!;
        CreateDirectoryOwnerOnly(directory);

        await File.WriteAllTextAsync(storagePath, spillable, cancellationToken);

        var previewLength = Math.Min(config.PreviewSizeChars, spillable.Length);
        var preview = $"{spillable[..previewLength]}\n... [{spillable.Length} chars persisted to {resultId}]";

        _logger.LogInformation(
            "Tool result {ResultId} from {ToolName} persisted to disk: {Length} chars at {Path}",
            resultId, toolName, spillable.Length, storagePath);

        return new ToolResultReference
        {
            ResultId = resultId,
            ToolName = toolName,
            Operation = operation,
            PreviewContent = preview,
            FullContentPath = storagePath,
            SizeChars = spillable.Length,
            Timestamp = timestamp
        };
    }

    /// <inheritdoc />
    public async Task<ToolResultPage> RetrievePageAsync(
        string resultId,
        string scopeId,
        int offset,
        int maxChars,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resultId);
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeId);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxChars);

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
        // writes to — rather than trusted from an in-memory index, for two reasons at once (#521):
        //   1. Ownership: a caller supplying a DIFFERENT scopeId than the one this result was stored
        //      under can never reach it, because the reconstructed path simply lands in that OTHER
        //      caller's own directory, which does not contain this resultId's file. No separate
        //      "does scopeId match what I recorded" check is needed or possible to skip.
        //   2. Durability: an in-memory index empties on every process restart, even though the file
        //      itself is still on disk — reconstructing the path means a restart no longer makes an
        //      already-spilled result unrecoverable.
        // SanitizeSessionSegment applies to scopeId for the identical path-traversal reason it already
        // applies to StoreIfLargeAsync's sessionId — this is the same path segment, read back.
        // Residual gap accepted (found in /simplify's review): reconstructing from CurrentValue rather
        // than a path captured at write time means a same-process hot reload of StoragePath between a
        // spill and its retrieval would point this read at a different root than the write used. Not
        // fixed — StoragePath is not a value anyone realistically hot-reloads mid-process.
        var safeScopeId = SanitizeSessionSegment(scopeId, nameof(scopeId));
        var config = _options.CurrentValue.AI.ContextManagement.ToolResultStorage;
        var storagePath = Path.Combine(config.StoragePath, safeScopeId, "tool-results", $"{resultId}.json");

        _logger.LogDebug(
            "Retrieving page (offset {Offset}, maxChars {MaxChars}) of result {ResultId} from {Path}",
            offset, maxChars, resultId, storagePath);

        string content;
        try
        {
            // #563: the whole stored file is read into memory rather than seeking within the stream.
            // Deliberately simple over deliberately fast: the file is already bounded to MaxSpillChars
            // by StoreIfLargeAsync (a few MB at the shipped default), so reading it whole per page costs
            // nothing that matters, and a plain in-memory Substring sidesteps every edge case a
            // character-counting stream-skip would otherwise have to get right (multi-byte encoding,
            // resuming a skip across buffer boundaries) for a bound that is not actually load-bearing.
            content = await File.ReadAllTextAsync(storagePath, cancellationToken);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // Deliberately the same exception, with the same message shape, whether resultId was never
            // stored at all or was stored under a DIFFERENT scope — see the interface's own remarks on
            // why "exists but not yours" must read identically to "does not exist".
            throw new KeyNotFoundException($"No stored result found for id '{resultId}'.");
        }

        var clampedOffset = Math.Min(offset, content.Length);
        var pageEnd = Math.Min(clampedOffset + maxChars, content.Length);

        // Never split a surrogate pair across a page boundary — the same guard BoundedText.Cap applies
        // when it cuts text, applied here because pagination cuts text the same way a single truncation
        // does, just repeatedly. Safe to apply unconditionally: a page's start offset is always either 0
        // or a prior page's NextOffset, which this same rule already guaranteed never lands mid-pair.
        if (pageEnd > clampedOffset && char.IsHighSurrogate(content[pageEnd - 1]))
        {
            pageEnd--;
        }

        return new ToolResultPage
        {
            Text = content[clampedOffset..pageEnd],
            NextOffset = pageEnd,
            TotalChars = content.Length
        };
    }

    /// <inheritdoc />
    public Task<int> PruneExpiredAsync(TimeSpan gracePeriod, CancellationToken cancellationToken = default)
    {
        var storagePath = _options.CurrentValue.AI.ContextManagement.ToolResultStorage.StoragePath;
        if (!Directory.Exists(storagePath))
            return Task.FromResult(0);

        // Age, not "is the owning scope gone" — this store has no way to ask that question (it does
        // not know what a conversation or a plan run is). See IToolResultStore.PruneExpiredAsync's own
        // remarks for why that is an acceptable trade here, unlike for a conversation budget row.
        var cutoffUtc = DateTime.UtcNow - gracePeriod;
        var removed = 0;

        foreach (var filePath in Directory.EnumerateFiles(storagePath, "*.json", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (File.GetLastWriteTimeUtc(filePath) >= cutoffUtc)
                continue;

            try
            {
                File.Delete(filePath);
                removed++;
                RemoveIfEmpty(Path.GetDirectoryName(filePath));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // One file's deletion failing (concurrently deleted, permissions) must not stop the
                // sweep from reclaiming everything else it can — the same must-not-throw discipline
                // ToolCallAdmissionPipeline applies to a spill failure.
                _logger.LogWarning(ex, "Failed to delete expired tool result at {Path}; will retry on the next sweep.", filePath);
            }
        }

        return Task.FromResult(removed);
    }

    /// <summary>
    /// Removes <paramref name="directory"/>, and its parent if that too is now empty, so a fully
    /// swept scope does not leave empty <c>tool-results</c>/scope directories behind forever.
    /// </summary>
    private static void RemoveIfEmpty(string? directory)
    {
        while (directory is not null && Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
        {
            try
            {
                Directory.Delete(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best-effort tidiness only — a directory that resists deletion (e.g. a concurrent
                // writer just claimed it) is not a reclaim failure; the files inside are already gone.
                return;
            }

            directory = Path.GetDirectoryName(directory);
        }
    }

    /// <summary>
    /// Creates <paramref name="directory"/> (and any missing parents) with owner-only access on POSIX
    /// (#559, pairs with #527) — the directory a spilled result's raw, unredacted-since-#563 output
    /// lands in. Windows is left to its inherited ACL, matching the only other place in this codebase
    /// that restricts filesystem permissions (<c>SandboxWorkspace</c>) rather than inventing a second,
    /// untested ACL-setting path.
    /// </summary>
    private static void CreateDirectoryOwnerOnly(string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(directory);
            return;
        }

        Directory.CreateDirectory(
            directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
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
    /// <see cref="RetrievePageAsync"/> (parameter <c>scopeId</c>); a hardcoded <c>nameof(sessionId)</c>
    /// would misreport the latter (a /code-review finding).
    /// </param>
    /// <returns>The validated session identifier, guaranteed to be a single path segment.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="sessionId"/> contains a character outside the allowed
    /// segment charset, is a relative directory reference ("." or ".."), or has a trailing dot
    /// Windows would silently strip.
    /// </exception>
    private static string SanitizeSessionSegment(string sessionId, string paramName)
    {
        // #560: a positive allowlist, not a growing list of rejected shapes. Every producer of a
        // scope id (durable conversation ids, plan/run ids, minted GUIDs, and any future caller) is
        // covered by construction — not just the specific traversal strings a prior review happened
        // to find — because anything outside this charset is refused outright. Neither path
        // separator is in the set, which is what closes traversal and UNC egress; a rooted absolute
        // path cannot match either. Checked first so a malformed id gets one clear rejection reason
        // rather than falling through to a more specific but less informative message below.
        if (!AllowedSegmentCharset.IsMatch(sessionId))
        {
            throw new ArgumentException(
                "Session ID must be 1-128 characters from [A-Za-z0-9_.:-].", paramName);
        }

        // "." and ".." are within the allowed charset but resolve to the storage root or a parent
        // when combined, so reject them explicitly.
        if (sessionId is "." or "..")
        {
            throw new ArgumentException(
                "Session ID must not be a relative directory reference.", paramName);
        }

        // Windows silently trims a trailing dot off a path segment, so "<id>" and "<id>." resolve to
        // the SAME directory there even though they compare unequal as strings — two different
        // scopes could collide onto one storage directory (a security-review finding). The allowed
        // charset permits a trailing dot, so this still needs its own check.
        if (sessionId.EndsWith('.'))
        {
            throw new ArgumentException(
                "Session ID must not have a trailing dot.", paramName);
        }

        return sessionId;
    }
}
