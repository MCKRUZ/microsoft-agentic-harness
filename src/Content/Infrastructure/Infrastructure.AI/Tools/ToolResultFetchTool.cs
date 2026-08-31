using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Context;
using Application.AI.Common.Interfaces.Tools;
using Domain.AI.Changes;
using Domain.AI.Models;
using Domain.AI.Sandbox;
using Domain.Common.Config;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Tools;

/// <summary>
/// Retrieves one bounded page of a tool result that was truncated and spilled to
/// <see cref="IToolResultStore"/> (#521, #563) — the model's way to ask for the rest of a result it
/// was only shown a cut-down version of, one page at a time.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Pages, not a single "full output" read (#563).</strong> A spilled result can be
/// considerably larger than what any single response to the model should carry, so this tool never
/// returns more than one bounded page per call — the same reasoning that bounds every other tool
/// result reaching the model applies to a fetched page too, since a page flows back through the
/// normal admission pipeline like any other tool output (see the last remark below). An optional
/// <c>offset</c> parameter (character offset, default <c>0</c>) selects where the next page starts;
/// a page whose read left more of the result unread carries a trailer naming the offset to pass next.
/// </para>
/// <para>
/// <strong>The retrieval scope comes from the CALLING request's own <see cref="IAgentExecutionContext"/>,
/// resolved per invocation, never from a caller-supplied parameter.</strong> A model-facing
/// <c>resultId</c> (plus the optional <c>offset</c>) are the only arguments this tool accepts;
/// widening that to also accept a scope id would let the model simply state whose data it wants to
/// read, defeating the isolation boundary <see cref="IToolResultStore"/> enforces.
/// </para>
/// <para>
/// <strong>Registered <c>Singleton</c>, like every other keyed tool — NOT <c>Scoped</c>.</strong> An
/// earlier version of this type constructor-injected <see cref="IAgentExecutionContext"/> directly and
/// was registered <c>AddKeyedScoped</c> specifically to avoid a captive-dependency leak. That reasoning
/// was correct but the mechanism was wrong for this codebase: every production caller resolves a keyed
/// <see cref="ITool"/> by name from a SINGLETON holding the ROOT service provider
/// (<c>ToolChainBuilder.ResolveToolByName</c>, <c>FirstPartyToolLookup.Resolve</c>) — there is no
/// per-request scope at resolution time to be scoped INTO. Every host enables
/// <c>ServiceProviderOptions.ValidateScopes</c>, so a scoped registration doesn't leak state across
/// callers the way a naive singleton constructor-injecting a scoped service would — it fails LOUDLY,
/// with <see cref="InvalidOperationException"/>, on every turn of every skill that lists this tool
/// (caught by this repo's own <c>correctness</c> gate before this shipped). The fix is the same pattern
/// already used throughout this codebase for a long-lived singleton that needs a per-request, scoped
/// service (see <see cref="IAmbientRequestScope"/>'s own remarks, and its dozen-plus other consumers):
/// resolve <see cref="IAgentExecutionContext"/> from <see cref="IAmbientRequestScope.Current"/> AT
/// EXECUTION TIME, inside <see cref="ExecuteAsync"/>, never at construction. <see cref="IToolResultStore"/>
/// itself stays constructor-injected directly — it is registered singleton, so singleton-into-singleton
/// is safe and needs no ambient indirection.
/// </para>
/// <para>
/// <strong>Deliberately not <see cref="ITool.IsDirectlyInvocable"/>.</strong> Direct HTTP invocation
/// mints a fresh <see cref="IAgentExecutionContext.ToolResultScopeId"/> for every single call (see
/// that property's remarks) — a result spilled during one direct invocation can never be fetched by a
/// later, unrelated one, because their scopes never match. That is not a bug on this tool's part; it
/// is the correct consequence of direct-invoke having no session for "later" to mean anything within.
/// Removing this tool from that surface avoids offering a call that can only ever answer "not found".
/// Direct invocation also does not run inside the MediatR pipeline that establishes the ambient request
/// scope, so <see cref="IAmbientRequestScope.Current"/> would be null there regardless.
/// </para>
/// <para>
/// A fetched PAGE is routed through the normal <c>ToolResult.Ok</c> return, so it flows back through
/// the same admission pipeline as any other tool result — sanitized and bounded exactly like any other
/// tool's output (#563). The page size this tool requests
/// (<see cref="Domain.Common.Config.AI.ContextManagement.ToolResultStorageConfig.PerResultCharLimit"/>
/// halved) stays comfortably under that ceiling specifically so a page is never itself truncated and
/// re-spilled by the pipeline it flows back through — sanitizing can only make text longer, never
/// shorter, so the margin needs to survive that growth too.
/// </para>
/// <para>
/// <strong>Redaction happens once, at spill time, over the complete stored content — never here, per
/// page.</strong> (Security-review finding, now on its third revision.) An earlier version of this
/// tool redacted each page individually, gated by a flag persisted alongside the content. That was
/// broken: a page boundary is a character offset the CALLER chooses (the model's own <c>offset</c>
/// argument), so a caller could split a secret across two page boundaries and recover both halves
/// unredacted — neither page alone contains a complete pattern for a redaction filter to match. There
/// is no fix for that which still redacts per page, because the caller can always choose a new split
/// point. A later revision moved redaction to write time but gated it on the originating call's own
/// classification — also broken, because a plain-allow call spilled raw, unscanned content.
/// <c>FileSystemToolResultStore.StoreIfLargeAsync</c> now redacts everything it persists,
/// unconditionally, before the write: whatever this tool reads back is already safe, and a page is
/// just a slice of it.
/// </para>
/// <para>
/// <strong>The injection/exfiltration scan is a DIFFERENT mechanism from redaction above, and is NOT
/// closed by write-time redaction.</strong> (Security-review finding.) That scan is not something the
/// store persists ahead of time — it runs once per CALL, on whatever text a call returns, as part of
/// the generic admission pipeline every tool result flows through (the last remark above). Before
/// pagination existed, "once per call" and "once for the whole result" were the same thing; #563 made
/// them different, since a single logical result now returns across many calls, each scanned in
/// isolation. A payload straddling the exact character offset one page ends at is therefore never
/// fully visible to either page's own scan. <see cref="ExecuteAsync"/> closes this the same way this
/// codebase closes every other "a cut boundary defeats a pattern match" case (compare
/// <c>FileSystemToolResultStore.RedactionScanMargin</c>, <c>ToolCallAdmissionPipeline.ScrubOverlapMargin</c>):
/// the offset it tells the model to resume from is pulled back by <see cref="PageScanOverlapMargin"/>
/// characters from where the page actually ended, so the NEXT call's own independent scan re-covers
/// this page's tail in full — any pattern up to that length crossing the true boundary is guaranteed
/// (standard overlapping-window argument) to be wholly contained in at least one of the two calls' own
/// scanned text, and can be caught there before that call's text is ever returned. The cost is a small,
/// fixed slice of text shown twice across two calls; there is no fix that avoids this while still
/// scanning strictly one page per call, the constraint every design in this subsystem preserves.
/// </para>
/// </remarks>
public sealed class ToolResultFetchTool : ITool
{
    public const string ToolName = "tool_result_fetch";

    // Security-review finding: same 8KB value as FileSystemToolResultStore.RedactionScanMargin and
    // ToolCallAdmissionPipeline.ScrubOverlapMargin — comfortably exceeds every pattern the injection/
    // exfiltration sanitizer (or the redaction filter, defense in depth) matches. See this type's own
    // remarks and ExecuteAsync for the overlapping-window argument this margin makes correct.
    private const int PageScanOverlapMargin = 8 * 1024;

    private static readonly IReadOnlyList<string> Operations = ["fetch"];

    private readonly IToolResultStore _resultStore;
    private readonly IAmbientRequestScope _ambientScope;
    private readonly IOptionsMonitor<AppConfig> _options;
    private readonly ILogger<ToolResultFetchTool> _logger;

    public ToolResultFetchTool(
        IToolResultStore resultStore,
        IAmbientRequestScope ambientScope,
        IOptionsMonitor<AppConfig> options,
        ILogger<ToolResultFetchTool> logger)
    {
        ArgumentNullException.ThrowIfNull(resultStore);
        ArgumentNullException.ThrowIfNull(ambientScope);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _resultStore = resultStore;
        _ambientScope = ambientScope;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => ToolName;

    /// <inheritdoc />
    public string Description =>
        "Retrieves one page of a tool result that was truncated. Pass the id from the " +
        $"\"{string.Format(Application.AI.Common.Services.Governance.ToolCallAdmissionPipeline.SpilledResultMarkerFormat, "...").Trim()}\" " +
        "marker as the 'resultId' parameter. If the page returned says more is available, call again " +
        "with the same 'resultId' and the 'offset' the page names to continue reading.";

    /// <inheritdoc />
    public IReadOnlyList<string> SupportedOperations => Operations;

    /// <inheritdoc />
    public bool IsReadOnly => true;

    /// <inheritdoc />
    public bool IsConcurrencySafe => true;

    /// <inheritdoc />
    public bool IsDirectlyInvocable => false;

    /// <inheritdoc />
    public BlastRadius RiskTier => BlastRadius.Trivial;

    /// <summary>
    /// The shipped <see cref="IToolResultStore"/> implementation is <c>FileSystemToolResultStore</c> —
    /// a spilled result is read back from disk, so <see cref="ToolCapability.FileRead"/> is the honest
    /// declaration rather than <see cref="ToolCapability.None"/> (Infrastructure.AI.Governance.Tests'
    /// <c>AllToolsCapabilityCoverageTests</c> fails a new tool that leaves this undeclared).
    /// </summary>
    public ToolCapability RequiredCapabilities => ToolCapability.FileRead;

    /// <inheritdoc />
    public async Task<ToolResult> ExecuteAsync(
        string operation,
        IReadOnlyDictionary<string, object?> parameters,
        CancellationToken cancellationToken = default)
    {
        if (!parameters.TryGetValue("resultId", out var raw)
            || raw is not string resultId
            || string.IsNullOrWhiteSpace(resultId))
        {
            return ToolResult.Fail("A non-empty 'resultId' parameter is required.");
        }

        if (!TryGetOffset(parameters, out var offset))
        {
            return ToolResult.Fail("The 'offset' parameter, when supplied, must be a non-negative integer.");
        }

        // Resolved here, not at construction — this instance is a process-lifetime singleton and the
        // execution context is scoped to the calling request. See this type's own remarks for why.
        var executionContext = _ambientScope.Current?.GetService<IAgentExecutionContext>();
        if (executionContext is null)
        {
            _logger.LogWarning(
                "tool_result_fetch invoked with no ambient request scope established; cannot resolve " +
                "the calling request's own retrieval scope.");
            return ToolResult.Fail("Unable to retrieve stored results outside an active agent turn.");
        }

        // #563: half PerResultCharLimit, read fresh per call rather than cached — a page this size stays
        // comfortably under the ceiling this tool's own result is about to be bounded to on the way back
        // out, even after sanitizing/redacting can only grow it. See this type's own remarks for why
        // that margin matters (a page that overflows the ceiling would itself be truncated and spilled
        // again, under a fresh id, rather than reaching the model as the page the caller asked for).
        var maxChars = Math.Max(
            1, _options.CurrentValue.AI.ContextManagement.ToolResultStorage.PerResultCharLimit / 2);

        try
        {
            var page = await _resultStore
                .RetrievePageAsync(resultId, executionContext.ToolResultScopeId, offset, maxChars, cancellationToken)
                .ConfigureAwait(false);

            // Redaction (if the originating call required it) already happened once, at spill time,
            // over the complete stored content — see this type's own remarks for why a page can never
            // safely be redacted a second time here. page.Text is already whatever it needs to be.
            var text = page.Text;

            if (!page.HasMore)
            {
                return ToolResult.Ok(text);
            }

            // Security-review finding: the resumption offset told to the model is pulled back by
            // PageScanOverlapMargin from where the page actually ended (page.NextOffset), not that
            // value itself — see this type's own remarks for the overlapping-window argument this
            // makes correct. Math.Max keeps forward progress even if maxChars is configured smaller
            // than the margin, mirroring RetrievePageAsync's own "never leave zero progress" guard.
            var resumeOffset = Math.Max(offset + 1, page.NextOffset - PageScanOverlapMargin);

            var trailer =
                $"\n[page ends at {page.NextOffset} of {page.TotalChars} chars — call tool_result_fetch " +
                $"again with id={resultId}, offset={resumeOffset}]";
            return ToolResult.Ok(text + trailer);
        }
        catch (KeyNotFoundException)
        {
            // Deliberately generic — see IToolResultStore.RetrievePageAsync's own remarks for why
            // "wrong scope" and "never existed" must be indistinguishable from outside the store.
            return ToolResult.Fail($"No stored result found for id '{resultId}'.");
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException
            or NotSupportedException or System.Security.SecurityException)
        {
            // A security review of #521 found these could otherwise escape ExecuteAsync as raw
            // exceptions — AIToolConverter invokes tools with no surrounding try/catch. The write side
            // (SpillAndBuildMarkerAsync) already degrades store failures instead of throwing; the read
            // side must give the same guarantee rather than letting a disk error fault the whole turn.
            _logger.LogWarning(ex, "Failed to retrieve tool result {ResultId}", resultId);
            return ToolResult.Fail($"No stored result found for id '{resultId}'.");
        }
    }

    /// <summary>
    /// Parses the optional, model-supplied <c>offset</c> parameter. Missing means "start from the
    /// beginning" (<c>0</c>); present but not a well-formed non-negative integer is refused outright
    /// rather than silently treated as omitted — a model that got the offset wrong should be told so,
    /// not have its request silently restarted from the beginning.
    /// </summary>
    private static bool TryGetOffset(IReadOnlyDictionary<string, object?> parameters, out int offset)
    {
        if (!parameters.TryGetValue("offset", out var raw) || raw is null)
        {
            offset = 0;
            return true;
        }

        var parsed = raw switch
        {
            int i => i,
            long l and >= 0 and <= int.MaxValue => (int)l,
            string s when int.TryParse(s, out var fromString) => fromString,
            _ => (int?)null
        };

        if (parsed is not { } value || value < 0)
        {
            offset = 0;
            return false;
        }

        offset = value;
        return true;
    }
}
