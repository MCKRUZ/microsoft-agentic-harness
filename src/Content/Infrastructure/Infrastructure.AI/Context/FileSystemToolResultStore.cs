using Application.AI.Common.Interfaces.Context;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Telemetry;
using Application.AI.Common.Services.Governance;
using Domain.AI.Context;
using Domain.AI.Telemetry.Redaction;
using Domain.Common.Config;
using Domain.Common.Helpers;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
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
    // /code-review + /simplify findings: the charset and shape checks below now live in
    // Domain.Common.Helpers.StorageSegmentSafety, shared with RunConversationCommandValidator and
    // RunOrchestratedTaskCommandValidator — see that type's own remarks for the ':'-admission and
    // 200-char-length history, and for why this is the fourth-and-hopefully-last hand-copy of this
    // check to exist. ':' is still admitted, deliberately, for PlanRunKeys.StepConversationId's
    // "{runScope}:{stepId}" shape; SanitizeSessionSegment's own Path.IsPathRooted check below (via
    // StorageSegmentSafety.HasUnsafeShape) remains the actual, independent backstop against the
    // single-letter-drive shape a bare ':' could otherwise produce.

    // /code-review finding: how far past MaxSpillChars a scan reads before redacting, so a secret whose
    // match starts before the true limit but extends past it is still fully present for the redaction
    // filter to match in full — see StoreIfLargeAsync's own remarks for the exact bug this closes.
    // 8KB mirrors ToolCallAdmissionPipeline.ScrubOverlapMargin's identical value and reasoning for the
    // model-facing ceiling; not literally shared (that constant is internal to a different assembly),
    // but the same margin comfortably exceeds every pattern DefaultContentRedactionFilter matches.
    private const int RedactionScanMargin = 8 * 1024;

    // #574: how long a page-fetch sequence's decoded file content stays cached after its most recent
    // page read, so walking every page of one spilled result costs one disk read instead of one per
    // page — see RetrievePageAsync's own remarks for the O(fileSize^2) cost this closes. Sized against
    // realistic back-to-back pagination (a model fetching the next page of the same result within a
    // few turns), not against how long a result stays retrievable at all — ToolResultRetentionConfig's
    // GracePeriod already governs that, and this is strictly shorter than it, so a cache miss always
    // falls back to a fresh, correct disk read rather than ever serving content past that window.
    // Mirrors AgentConversationCache's identical sliding-expiration shape for the same reason: an
    // IMemoryCache entry with no activity should give its memory back rather than pin a potentially
    // multi-megabyte string forever.
    private static readonly TimeSpan PageCacheSlidingExpiration = TimeSpan.FromMinutes(5);

    private readonly IOptionsMonitor<AppConfig> _options;
    private readonly ICompositeResponseSanitizer _sanitizer;
    private readonly IContentRedactionFilter _redactionFilter;
    private readonly IMemoryCache _pageCache;
    private readonly ILogger<FileSystemToolResultStore> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="FileSystemToolResultStore"/> class.
    /// </summary>
    /// <param name="options">Application configuration for storage thresholds and paths.</param>
    /// <param name="sanitizer">
    /// Applied, unconditionally, to every large result's content before it is written to disk, before
    /// <paramref name="redactionFilter"/> runs — see <see cref="StoreIfLargeAsync"/>'s own remarks for
    /// why the injection/exfiltration scan this performs must happen here, at write time, and never
    /// per fetched page.
    /// </param>
    /// <param name="redactionFilter">
    /// Applied, unconditionally, to every large result's content before it is written to disk — see
    /// <see cref="StoreIfLargeAsync"/>'s own remarks for why this happens at write time, every time,
    /// and never at read time.
    /// </param>
    /// <param name="pageCache">
    /// Backs a short-lived decoded-content cache for <see cref="RetrievePageAsync"/> (#574) — see that
    /// method's own remarks for why re-reading the whole file per page was replaced with this. A
    /// DEDICATED, size-bounded instance keyed <c>"tool-result-page-cache"</c> (security-review finding)
    /// — never the ambient app-wide <see cref="IMemoryCache"/> singleton other consumers
    /// (e.g. <c>AgentConversationCache</c>) share, so a pathological fetch pattern here cannot pin an
    /// unbounded amount of memory in a cache other subsystems also depend on.
    /// </param>
    /// <param name="logger">Logger for storage diagnostics.</param>
    public FileSystemToolResultStore(
        IOptionsMonitor<AppConfig> options,
        ICompositeResponseSanitizer sanitizer,
        IContentRedactionFilter redactionFilter,
        [FromKeyedServices("tool-result-page-cache")] IMemoryCache pageCache,
        ILogger<FileSystemToolResultStore> logger)
    {
        _options = options;
        _sanitizer = sanitizer;
        _redactionFilter = redactionFilter;
        _pageCache = pageCache;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<ToolResultReference> StoreIfLargeAsync(
        string sessionId,
        string toolName,
        string? operation,
        string fullOutput,
        bool scopeIsRetrievable,
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

        // #575: the single chokepoint for "can a spilled file here ever be fetched back" — previously
        // checked independently at each call site (ToolCallAdmissionPipeline.SpillAndBuildMarkerAsync,
        // ToolOutputCompressionBehavior.Handle both re-derived IAgentExecutionContext.
        // HasRetrievableToolResultScope and skipped calling this method entirely), leaving a future
        // third caller to remember to re-derive the same check or silently leak an unreachable file to
        // disk forever. scopeIsRetrievable has no default value specifically so a new caller cannot
        // silently inherit a permissive assumption — see this parameter's own interface remarks. Both
        // production callers ALSO keep their own early return before this method is invoked at all
        // (a genuine, separate optimization: it avoids building the full output — a factory call, a
        // redaction pass, a compression pass — for content that would just be discarded here), so this
        // check is reached today only when a caller has already confirmed it is true; it is the backstop
        // for whoever does not.
        if (!scopeIsRetrievable)
        {
            _logger.LogDebug(
                "Tool result {ResultId} from {ToolName} has no retrievable scope — keeping inline " +
                "regardless of size ({Length} chars)",
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

        // /code-review finding, fourth revision: the third revision capped to MaxSpillChars BEFORE
        // redacting, so a secret whose match straddled that exact cut point lost its tail before the
        // redaction filter ever saw it — the identical "boundary a cut creates defeats a pattern match"
        // shape as the page-splitting bypass the third revision closed, just moved to the write-side
        // truncation boundary instead of a read-side page boundary. Fixed the same way this codebase's
        // own ToolCallAdmissionPipeline.PreCutForScan/ScrubOverlapMargin already fixes it for the
        // model-facing ceiling: cap to a WIDER region first (MaxSpillChars + RedactionScanMargin) so a
        // secret near the true limit is still fully present for the filter to match in full, redact
        // that whole region, then cut the ALREADY-REDACTED text down to the true MaxSpillChars. The
        // final cut can never re-expose a raw secret, because redaction has already replaced it with a
        // placeholder before that cut ever runs.
        var scanCeiling = config.MaxSpillChars <= int.MaxValue - RedactionScanMargin
            ? config.MaxSpillChars + RedactionScanMargin
            : int.MaxValue;
        var (scanRegion, _) = BoundedText.Cap(fullOutput, scanCeiling, marker: "");

        // Security-review finding: the injection/exfiltration scan is a DIFFERENT mechanism from the
        // secret redaction below, and closing the redaction boundary-split bug (this method's own
        // remarks) did nothing for it. That scan otherwise runs once per model-facing CALL
        // (ToolCallAdmissionPipeline), and #563 gave a single logical result many such calls once it
        // started paginating — a payload straddling the exact character offset one page ends at was
        // never fully visible to either page's own scan. A read-side fix (overlap the page a caller
        // resumes from) was tried and rejected: it depends on the CALLER using the offset this store
        // suggests, and the model is exactly the caller an injection payload could try to manipulate
        // into requesting a different one — the same "caller can always choose a new split point" shape
        // this codebase already rejected a per-page fix for on the redaction side (see the second
        // revision noted below). Sanitizing here, unconditionally, over the same widened scan region,
        // before ANY page boundary exists, closes the SPECIFIC bypass this revision targets — a
        // page-fetch offset the caller chooses can no longer split a pattern that no longer exists in
        // the stored copy. It does not, by itself, make a pattern's own MATCH window unbounded: a
        // sanitizer rule that itself requires two markers within one scan (e.g. an opening and closing
        // HTML comment tag) can still be defeated by padding between them past scanCeiling, the same
        // scan-window limitation ToolCallAdmissionPipeline's own bounded pre-cut already had before
        // this revision, just at a much larger default ceiling now (MaxSpillChars, not the model-facing
        // ceiling). Tracked as a follow-up rather than fixed here: raising the ceiling further trades
        // directly against unbounded regex scan cost on attacker-controlled input, the same tension
        // SanitizeThenRedact.MaxScanLength already exists to bound elsewhere, and resolving it properly
        // means redesigning the affected patterns, not widening this one call site further.
        // Sanitize runs BEFORE redact, mirroring ToolResultText.SanitizeAndRedact's own ordering: an
        // injection payload is stripped before the now-shorter, already-inert text is scanned for
        // secret patterns.
        //
        // Security-review finding: a sanitizer is consumer-replaceable (ICompositeResponseSanitizer),
        // and ToolResultText.SanitizeText already treats a non-null-in/null-or-empty-out result as a
        // contract break worth a visible placeholder rather than silently persisting empty content —
        // this call site did not, so a non-conforming or overly aggressive custom implementation could
        // silently discard a large tool result with no warning and no recoverable trace of it.
        // /simplify finding: the same placeholder text as ToolResultText.CorruptedSanitizerOutputPlaceholder
        // — not literally shared (that constant is internal to a different assembly, and the shared
        // SanitizeThenRedact.Apply combinator that also carries this guard hardcodes a 64KB scan ceiling
        // this call site cannot use — see the scanCeiling remarks above) — kept in identical wording so
        // the two are recognizable as the same guarantee if either one changes.
        var sanitizeResult = _sanitizer.Sanitize(scanRegion, toolName);
        var sanitizerReturnedEmpty =
            string.IsNullOrEmpty(sanitizeResult.SanitizedContent) && !string.IsNullOrEmpty(scanRegion);
        if (sanitizerReturnedEmpty)
        {
            _logger.LogWarning(
                "ICompositeResponseSanitizer returned empty content for a non-empty tool result from " +
                "{ToolName}; persisting a placeholder instead of silently discarding it",
                toolName);
        }
        var sanitized = sanitizerReturnedEmpty
            ? "[tool result withheld: the response sanitizer returned no content]"
            : sanitizeResult.SanitizedContent;

        // /simplify finding: this sanitize-then-redact sequence deliberately does not call the shared
        // SanitizeThenRedact.Apply combinator that every other sanitize-then-redact site in this
        // codebase shares (#470) — its MaxScanLength (64KB) exists for exactly the ReDoS-cost reason
        // scanCeiling's own remarks above discuss, but is far smaller than MaxSpillChars (default a
        // few MB): routing through it here would silently shrink write-time redaction's effective
        // coverage from the whole spilled result down to its first 64KB, regressing a guarantee that
        // predates this revision.
        //
        // Security-review finding, third revision: redaction happens HERE, unconditionally, before the
        // write — never gated on the originating call's own classification, and never at read time.
        // Two prior revisions both regressed a guarantee this repo already had:
        //   1st revision (pre-#563, on main): redacted the disk copy unconditionally with
        //      RedactionCategories.All, on the reasoning that persisting to disk is a stronger exposure
        //      than showing a model its own tool's output, so it is not optional the way the
        //      model-facing decision is.
        //   2nd revision (#563): stopped redacting at rest at all, and instead redacted each fetched
        //      PAGE independently at READ time, gated by a flag carried alongside the content — broken,
        //      because a page boundary is a character offset the CALLER chooses (tool_result_fetch's
        //      own model-supplied 'offset'), so a secret split across two page boundaries came back
        //      unredacted from both halves; no per-page fix closes that, since the caller can always
        //      choose a new split point.
        //   3rd revision (this one, security-review finding on the 2nd): moved redaction back to write
        //      time — closing the page-boundary bypass, since no boundary exists yet when this runs —
        //      but initially GATED it on the originating call's own admission.RedactsOutput, which
        //      regressed the 1st revision's unconditional guarantee for the common, unclassified
        //      plain-allow path. Unconditional, matching the 1st revision, closes both bypasses at once:
        //      no gate for an adversarial classification to sit outside of, and no page boundary for an
        //      adversarial offset to split across.
        var redacted = _redactionFilter.Redact(sanitized, RedactionCategories.All);

        // #563: bound what actually reaches disk to MaxSpillChars, no matter how large the tool's true
        // output was — an unbounded write is the exact "no silent caps" gap this cap exists to close,
        // just at the disk boundary instead of the context-window one. Cut here, on the ALREADY-REDACTED
        // text (see the widen-then-redact-then-shrink remark above for why this order matters).
        // BoundedText.Cap with an empty marker reuses its surrogate-pair-safe cut rather than a
        // hand-rolled Substring, and is a no-op whenever the redacted region is already within the cap.
        // /code-review finding: this flag drives an operator warning only (it never affects what gets
        // persisted or returned), but must reflect whatever Cap actually cut, not a re-derived
        // approximation. fullOutput.Length > MaxSpillChars looks equivalent but isn't: redaction can
        // INFLATE length (a secret becomes a longer placeholder), so content that was under
        // MaxSpillChars raw could still get real content cut here after redaction grew it past the
        // cap — a case the re-derived check would silently miss, suppressing a warning that should
        // have fired.
        var (spillable, spillTruncated) = BoundedText.Cap(redacted, config.MaxSpillChars, marker: "");
        if (spillTruncated)
        {
            _logger.LogWarning(
                "Tool result {ResultId} from {ToolName} is {Length} chars, exceeding MaxSpillChars "
                + "({MaxSpillChars}) — only the first {MaxSpillChars} chars are persisted and "
                + "retrievable; the rest is unrecoverable",
                resultId, toolName, fullOutput.Length, config.MaxSpillChars, config.MaxSpillChars);
        }

        var storagePath = Path.Combine(config.StoragePath, safeSessionId, "tool-results", $"{resultId}.txt");
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
        var storagePath = Path.Combine(config.StoragePath, safeScopeId, "tool-results", $"{resultId}.txt");

        _logger.LogDebug(
            "Retrieving page (offset {Offset}, maxChars {MaxChars}) of result {ResultId} from {Path}",
            offset, maxChars, resultId, storagePath);

        // #574: reading the whole stored file into memory on EVERY page was replaced with a short-lived
        // cache of the decoded content — the same IMemoryCache-plus-sliding-expiration shape
        // AgentConversationCache already uses. Walking a full MaxSpillChars spill (a few MB) in
        // maxChars-sized pages previously cost one whole-file read PER page — an O(fileSize) read
        // repeated once per page, ~O(fileSize^2 / pageSize) total I/O across a full walk. The first page
        // of a fetch sequence populates the cache; every later page in that same sequence is a cache
        // hit. A miss (cold cache, or the sliding window lapsed between pages) falls back to a fresh
        // disk read, so this is purely a cost optimization — correctness never depends on the cache
        // being warm.
        //
        // Keyed by the resolved storagePath, not by (scopeId, resultId) directly — a code-review finding
        // on the first cut of this fix: PruneExpiredAsync only ever has a raw file PATH in hand (from
        // Directory.EnumerateFiles), never the original pre-sanitization scopeId a (scopeId, resultId)
        // key would need to evict the matching entry. A scope id containing ':' (PlanRunKeys.
        // StepConversationId's "{runScope}:{stepId}" shape, admitted by AllowedCharset) is rewritten to
        // '~' on disk by SanitizeSessionSegment, and that rewrite is one-directional by design — so
        // reconstructing a (scopeId, resultId) cache key FROM a directory name can silently miss the
        // real entry for any scope id that used a colon. storagePath has no such ambiguity: it is what
        // both this method and StoreIfLargeAsync already compute deterministically from the same
        // sanitized segments, and it is exactly what PruneExpiredAsync's own sweep loop already holds
        // as `filePath` — see this method's remarks for why reconstructing a path deterministically,
        // rather than trusting an in-memory index, is this store's standing design already.
        if (!_pageCache.TryGetValue(storagePath, out string? content) || content is null)
        {
            try
            {
                // Bare text, not a JSON envelope — #563's second revision moved redaction to write time
                // (see StoreIfLargeAsync's own remarks), so nothing besides the content itself ever needs
                // to travel alongside it on disk.
                content = await File.ReadAllTextAsync(storagePath, cancellationToken);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                // Deliberately the same exception, with the same message shape, whether resultId was
                // never stored at all or was stored under a DIFFERENT scope — see the interface's own
                // remarks on why "exists but not yours" must read identically to "does not exist".
                throw new KeyNotFoundException($"No stored result found for id '{resultId}'.");
            }

            // Security-review finding: Size must be set explicitly (in characters, the same unit the
            // dedicated cache's own SizeLimit is expressed in — see this constructor's own remarks) so
            // the cache can actually enforce that limit. Without it, every entry costs nothing toward
            // SizeLimit as far as the cache is concerned, and a SizeLimit with no per-entry Size is not
            // a bound at all.
            _pageCache.Set(
                storagePath,
                content,
                new MemoryCacheEntryOptions { SlidingExpiration = PageCacheSlidingExpiration, Size = content.Length });
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

        // Correctness-review finding: StoragePath defaults to ".agent-sessions", the harness's whole
        // shared state root (governance/egress/escalation/changes/delegations all live under it,
        // per FoundryHostBootstrap) — NOT a directory this store owns exclusively. Enumerating
        // "*.txt" with AllDirectories under that root would delete any *.txt a future consumer adds
        // anywhere in that tree. Only ever descend into the exact shape StoreIfLargeAsync writes:
        // {StoragePath}/{scope}/tool-results/{resultId}.txt — one level of scope directories, each
        // with exactly one "tool-results" child.
        foreach (var scopeDir in Directory.EnumerateDirectories(storagePath))
        {
            var toolResultsDir = Path.Combine(scopeDir, "tool-results");
            if (!Directory.Exists(toolResultsDir))
                continue;

            foreach (var filePath in Directory.EnumerateFiles(toolResultsDir, "*.txt"))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (File.GetLastWriteTimeUtc(filePath) >= cutoffUtc)
                    continue;

                try
                {
                    File.Delete(filePath);
                    removed++;

                    // #574 code-review finding: the page-fetch cache is keyed by this same storagePath
                    // (see RetrievePageAsync's own remarks) but nothing evicted it when the backing file
                    // was reclaimed. Left unevicted, a page fetch within the cache's 5-minute sliding
                    // window could keep serving "reclaimed" content past the point retention was
                    // supposed to make it unrecoverable — a real behavioral regression versus reading
                    // straight from disk every time, which always failed consistently once a file was
                    // gone. IMemoryCache.Remove is a no-op when the key was never cached, so this is
                    // safe to call unconditionally rather than checking for a hit first.
                    _pageCache.Remove(filePath);
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
        // Security-review finding: Path.GetFullPath preserves a trailing separator on its input but
        // Path.GetDirectoryName (what advances "directory" each iteration below) always strips one —
        // measured directly against this SDK: GetFullPath("C:/temp/spills/") is "C:\temp\spills\" while
        // GetFullPath("C:/temp/spills") is "C:\temp\spills". A StoragePath configured WITH a trailing
        // separator therefore never string-equals any ancestor this loop climbs to, so the "never climbs
        // to or above root" guard this method's own remarks promise would silently never fire. Trimming
        // both sides makes the comparison independent of how the caller happened to write the config
        // value.
        var rootFullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

        while (!string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)),
                rootFullPath,
                StringComparison.OrdinalIgnoreCase)
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
        // /code-review finding: this method calls StorageSegmentSafety's three shape checks
        // INDIVIDUALLY rather than through the combined HasUnsafeShape — deliberately, not an
        // oversight: each check here throws its own distinct ArgumentException message, which
        // HasUnsafeShape's single boolean cannot express. PlanRunExecutor's RunId check uses
        // HasUnsafeShape directly because it only needs one generic rejection outcome. A future shape
        // rule added to HasUnsafeShape must also be added HERE (and to both command validators'
        // identical .Must() chains) for the same reason — this is exactly the "shared logic, per-site
        // wiring" shape that has already drifted twice on this allowlist; do not assume adding a rule
        // to HasUnsafeShape alone is sufficient.
        //
        // #560: a positive allowlist, not a growing list of rejected shapes. Every producer of a
        // scope id (durable conversation ids, plan/run ids, minted GUIDs, and any future caller) is
        // covered by construction — not just the specific traversal strings a prior review happened
        // to find — because anything outside this charset is refused outright. Neither path
        // separator is in the set, which is what closes traversal and UNC egress. Checked first so a
        // malformed id gets one clear rejection reason rather than falling through to a more specific
        // but less informative message below.
        if (!StorageSegmentSafety.AllowedCharset.IsMatch(sessionId))
        {
            throw new ArgumentException(
                "Session ID must be 1-200 characters from [A-Za-z0-9_.:-].", paramName);
        }

        // Independent of the charset above, deliberately — see StorageSegmentSafety's own remarks.
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
        if (StorageSegmentSafety.IsRelativeDirectoryReference(sessionId))
        {
            throw new ArgumentException(
                "Session ID must not be a relative directory reference.", paramName);
        }

        // Windows silently trims a trailing dot off a path segment, so "<id>" and "<id>." resolve to
        // the SAME directory there even though they compare unequal as strings — two different
        // scopes could collide onto one storage directory (a security-review finding). The allowed
        // charset permits a trailing dot, so this still needs its own check.
        if (StorageSegmentSafety.HasTrailingDot(sessionId))
        {
            throw new ArgumentException(
                "Session ID must not have a trailing dot.", paramName);
        }

        // Correctness-review finding: ':' passes the charset above (needed for
        // PlanRunKeys.StepConversationId's "{runScope}:{stepId}" shape — see AllowedSegmentCharset's
        // remarks) but Windows refuses it as a directory-NAME character outside the single-letter
        // drive-separator position the Path.IsPathRooted check above already excludes. Measured
        // directly against this SDK: Directory.CreateDirectory("conv-1:5") throws IOException
        // ("The directory name is invalid") on Windows, so every plan-step spill silently degraded to
        // a plain truncation marker there — the exact platform this repo develops on. '~' can never
        // appear in a value that already passed the charset check (it isn't in AllowedSegmentCharset),
        // so this 1:1 substitution is unambiguous with no reverse mapping needed: the same scope id
        // always encodes to the same directory name, on both the store and retrieve paths that both
        // call this method, and the substitution changes neither length nor legality on either OS.
        return sessionId.Replace(':', '~');
    }
}
