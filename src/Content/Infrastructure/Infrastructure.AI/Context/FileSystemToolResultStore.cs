using System.Text.Json;
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
    // ids, plan/run ids, minted GUIDs, and PlanRunKeys.StepConversationId's "{runScope}:{stepId}"
    // shape all fit it), so anything outside it is refused outright rather than pattern-matched
    // against known-dangerous cases. Neither '/' nor '\' — the only two path separators across
    // platforms — are in the set.
    //
    // ':' IS admitted, deliberately, after a real regression: an earlier version of this allowlist
    // excluded it specifically because Path.IsPathRooted("C:") and Path.IsPathRooted("C:foo") measure
    // TRUE on Windows — but that same exclusion also rejected PlanRunKeys.StepConversationId's
    // "{runScope}:{stepId}" shape, which every LLM step of every plan run produces, failing every one
    // of them at RunConversationCommandValidator. Measured directly (Path.IsPathRooted, this SDK):
    // "C:"/"C:foo"/"D:x" → true, but "conv-1:5"/"abc123:step-5"/"AB:foo" → false — Windows treats a
    // colon as a drive separator only when it is preceded by EXACTLY one ASCII letter, never when
    // preceded by two or more characters. A colon used as an internal separator between multi-character
    // segments therefore cannot produce a rooted path. The charset does not need to encode that
    // distinction itself: SanitizeSessionSegment's own Path.IsPathRooted check below is the actual,
    // independent backstop against the single-letter-drive shape, regardless of what this charset
    // admits — see its remarks.
    //
    // Length bound is 200, not 128 — a second real regression on the same allowlist. 128 matched
    // IPlanRunExecutor.MaxAgentIdLength, the cap on a bare ConversationId/RunId, but
    // PlanRunKeys.StepConversationId derives "{runScope}:{stepId}" from that value — up to
    // 128 + 1 + 36 (a Guid's default ToString length) = 165 characters — so a legal 92+ char run
    // scope produced a derived id this allowlist itself rejected. 200 covers the exact 165-char
    // worst case with margin, while staying well under typical single-path-segment filesystem limits.
    //
    // Anchored with \z, not $ — $ in .NET regex matches immediately before a trailing '\n' as well as
    // at the true end of the string, so "abc\n" would otherwise pass this pattern (a security-review
    // finding: a caller-supplied id ending in a newline becoming a directory name and then a log line,
    // the exact shape IsWellFormedAgentId's own per-character check avoids by construction). \z matches
    // only the absolute end.
    private static readonly Regex AllowedSegmentCharset =
        new(@"\A[A-Za-z0-9_.:-]{1,200}\z", RegexOptions.Compiled);

    /// <summary>
    /// The on-disk shape of a persisted result (security-review finding on #563). A bare text file
    /// cannot carry <see cref="ToolResultPage.RedactOnRetrieve"/> alongside its content, and that flag
    /// is exactly what closes the redaction-bypass finding — see that property's remarks. Only the
    /// disk-persisted branch of <see cref="StoreIfLargeAsync"/> uses this; the inline branch returns
    /// the caller's own string directly and nothing is ever paged back for it.
    /// </summary>
    private sealed record StoredResultEnvelope
    {
        public required string Content { get; init; }
        public bool RedactOnRetrieve { get; init; }
    }

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
        CancellationToken cancellationToken = default,
        bool redactOnRetrieve = false)
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

        // Security-review finding on #563: wrapped in an envelope, not written as bare text, so
        // RedactOnRetrieve travels with the content instead of being lost — see StoredResultEnvelope's
        // own remarks.
        var envelope = new StoredResultEnvelope { Content = spillable, RedactOnRetrieve = redactOnRetrieve };
        await File.WriteAllTextAsync(storagePath, JsonSerializer.Serialize(envelope), cancellationToken);

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

        StoredResultEnvelope envelope;
        try
        {
            // #563: the whole stored file is read into memory rather than seeking within the stream.
            // Deliberately simple over deliberately fast: the file is already bounded to MaxSpillChars
            // by StoreIfLargeAsync (a few MB at the shipped default), so reading it whole per page costs
            // nothing that matters, and a plain in-memory Substring sidesteps every edge case a
            // character-counting stream-skip would otherwise have to get right (multi-byte encoding,
            // resuming a skip across buffer boundaries) for a bound that is not actually load-bearing.
            var raw = await File.ReadAllTextAsync(storagePath, cancellationToken);
            envelope = JsonSerializer.Deserialize<StoredResultEnvelope>(raw)
                ?? throw new KeyNotFoundException($"No stored result found for id '{resultId}'.");
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or JsonException)
        {
            // Deliberately the same exception, with the same message shape, whether resultId was never
            // stored at all or was stored under a DIFFERENT scope — see the interface's own remarks on
            // why "exists but not yours" must read identically to "does not exist". A malformed envelope
            // (JsonException) is folded into the same refusal for the identical reason: it must not tell
            // a caller anything more specific than "not found".
            throw new KeyNotFoundException($"No stored result found for id '{resultId}'.");
        }

        var content = envelope.Content;
        var clampedOffset = Math.Min(offset, content.Length);
        var pageEnd = Math.Min(clampedOffset + maxChars, content.Length);

        // Never split a surrogate pair across a page boundary — the same guard BoundedText.Cap applies
        // when it cuts text, applied here because pagination cuts text the same way a single truncation
        // does, just repeatedly. Safe to apply unconditionally: a page's start offset is always either 0
        // or a prior page's NextOffset, which this same rule already guaranteed never lands mid-pair.
        if (pageEnd > clampedOffset && char.IsHighSurrogate(content[pageEnd - 1]))
        {
            pageEnd--;

            // Correctness-review finding: pulling back must never leave zero progress while content
            // remains, or a caller retrying with the returned NextOffset (== the offset it just sent)
            // gets the identical empty page forever. Reachable at maxChars as small as 1 landing
            // exactly on a pair's high surrogate. Push forward past the whole pair instead — the one
            // situation where a page is allowed to exceed maxChars, and only by one character.
            if (pageEnd == clampedOffset)
                pageEnd = Math.Min(clampedOffset + 2, content.Length);
        }

        return new ToolResultPage
        {
            Text = content[clampedOffset..pageEnd],
            NextOffset = pageEnd,
            TotalChars = content.Length,
            RedactOnRetrieve = envelope.RedactOnRetrieve
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

        // Correctness-review finding: StoragePath defaults to ".agent-sessions", the harness's whole
        // shared state root (governance/egress/escalation/changes/delegations all live under it,
        // per FoundryHostBootstrap) — NOT a directory this store owns exclusively. Enumerating
        // "*.json" with AllDirectories under that root would delete any *.json a future consumer adds
        // anywhere in that tree. Only ever descend into the exact shape StoreIfLargeAsync writes:
        // {StoragePath}/{scope}/tool-results/{resultId}.json — one level of scope directories, each
        // with exactly one "tool-results" child.
        foreach (var scopeDir in Directory.EnumerateDirectories(storagePath))
        {
            var toolResultsDir = Path.Combine(scopeDir, "tool-results");
            if (!Directory.Exists(toolResultsDir))
                continue;

            foreach (var filePath in Directory.EnumerateFiles(toolResultsDir, "*.json"))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (File.GetLastWriteTimeUtc(filePath) >= cutoffUtc)
                    continue;

                try
                {
                    File.Delete(filePath);
                    removed++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // One file's deletion failing (concurrently deleted, permissions) must not stop
                    // the sweep from reclaiming everything else it can — the same must-not-throw
                    // discipline ToolCallAdmissionPipeline applies to a spill failure.
                    _logger.LogWarning(ex, "Failed to delete expired tool result at {Path}; will retry on the next sweep.", filePath);
                }
            }

            RemoveIfEmptyUpTo(toolResultsDir, storagePath);
        }

        return Task.FromResult(removed);
    }

    /// <summary>
    /// Removes <paramref name="directory"/>, and its parent if that too is now empty, so a fully
    /// swept scope does not leave empty <c>tool-results</c>/scope directories behind forever. Never
    /// climbs to or above <paramref name="root"/> — a security-review finding on an earlier version
    /// of this method noted its only stop condition was "the directory is empty", so once the last
    /// spilled file anywhere was reclaimed it would keep deleting empty ancestors past the storage
    /// root the caller never asked it to touch.
    /// </summary>
    private static void RemoveIfEmptyUpTo(string directory, string root)
    {
        var rootFullPath = Path.GetFullPath(root);

        while (!string.Equals(Path.GetFullPath(directory), rootFullPath, StringComparison.OrdinalIgnoreCase)
            && Directory.Exists(directory)
            && !Directory.EnumerateFileSystemEntries(directory).Any())
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

            var parent = Path.GetDirectoryName(directory);
            if (parent is null)
                return;
            directory = parent;
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
        // separator is in the set, which is what closes traversal and UNC egress. Checked first so a
        // malformed id gets one clear rejection reason rather than falling through to a more specific
        // but less informative message below.
        if (!AllowedSegmentCharset.IsMatch(sessionId))
        {
            throw new ArgumentException(
                "Session ID must be 1-200 characters from [A-Za-z0-9_.:-].", paramName);
        }

        // Independent of the charset above, deliberately — see AllowedSegmentCharset's own remarks.
        // A colon is admitted by the charset (needed for PlanRunKeys.StepConversationId's
        // "{runScope}:{stepId}" shape), but Path.IsPathRooted still measures a bare drive reference
        // like "C:" or "C:foo" as TRUE on Windows, and Path.Combine discards every earlier segment
        // once one is rooted, writing outside StoragePath entirely. This check is what actually
        // enforces "never rooted" — it does not depend on the charset excluding the character that
        // makes a rooted form possible, which is what made this reachable the first time.
        if (Path.IsPathRooted(sessionId))
        {
            throw new ArgumentException(
                "Session ID must not be an absolute or drive-rooted path.", paramName);
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
