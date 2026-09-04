using System.Text.Json;
using System.Text.Json.Nodes;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Telemetry;
using Domain.AI.Governance;
using Domain.AI.Telemetry.Redaction;
using Microsoft.Extensions.AI;

namespace Application.AI.Common.Services.Governance;

/// <summary>
/// Transforms the plain text a tool result carries, in whichever shape it reaches a policy boundary
/// in, without losing that shape.
/// </summary>
/// <remarks>
/// <para>
/// Unwrapping a <see cref="JsonElement"/> into a bare string here would be a silent contract break: the
/// model-facing chat client sends a raw string to the model verbatim, but JSON-serializes — and so
/// re-quotes — a <c>JsonElement</c>. Returning transformed text in the wrong shape would change how the
/// model reads quotes, newlines, and other characters in the result, not just scrub it.
/// </para>
/// <para>
/// Handles five shapes a tool result can arrive in: a raw <see langword="string"/> and a serialized
/// JSON string element cover a keyed-DI/skill (<c>ITool</c>-backed) success. An MCP tool's success
/// reaches this boundary differently — <c>McpClientTool.InvokeCoreAsync</c> returns a bare
/// <see cref="TextContent"/> for a single-content-block result and an <see cref="AIContent"/> array for
/// a multi-block one, falling back to a serialized <c>CallToolResult</c> (a structured
/// <see cref="JsonElement"/> carrying its own <c>content</c> array) only when the result carries
/// structured content or protocol metadata — see #483, which closed that fifth shape.
/// </para>
/// </remarks>
internal static class ToolResultText
{
    /// <summary>
    /// Substituted when a sanitizer reports it changed something but returns no text to show for it — a
    /// runtime contract break <see cref="ICompositeResponseSanitizer"/> doesn't enforce against a
    /// consumer-supplied implementation. Every caller of <see cref="Sanitize(object?, ICompositeResponseSanitizer, string)"/> relies on a must-not-throw
    /// contract (see <c>GovernedAIFunction</c>'s and <c>DirectToolInvoker</c>'s own remarks); degrading to
    /// a visible placeholder here, the same way <c>ReportedFailureText</c> does for its own sanitizer
    /// dependency, keeps that contract rather than propagating an exception out of nearly every tool call
    /// this fix now touches.
    /// </summary>
    private const string CorruptedSanitizerOutputPlaceholder =
        "[tool result withheld: the response sanitizer returned no content]";

    /// <summary>
    /// Runs <paramref name="result"/>'s text through <paramref name="sanitizer"/> and returns it in the
    /// same shape it arrived — unless nothing changed, in which case <paramref name="result"/> is
    /// returned untouched rather than paying for a reconstruction that would only reproduce an
    /// equivalent value. A structured or unrecognized result is returned unchanged: a sanitizer operates
    /// on free text, and rewriting the raw text of a structured value risks producing a malformed result
    /// the model then mis-parses.
    /// </summary>
    public static object? Sanitize(object? result, ICompositeResponseSanitizer sanitizer, string toolName) =>
        Transform(result, text => SanitizeText(text, sanitizer, toolName));

    /// <summary>
    /// String-typed overload of <see cref="Sanitize(object?, ICompositeResponseSanitizer, string)"/> for a
    /// caller that already knows its content is plain text, not a shape needing preservation — the
    /// <c>ToolCallAdmissionPipeline.TryApplyTextOutputPolicy</c> boundary.
    /// </summary>
    /// <remarks>
    /// <strong>Non-null input always produces non-null output.</strong> <c>Transform</c>'s <c>case
    /// string</c> arm can only return <paramref name="content"/> itself or a sanitized string — never
    /// null, never a different type — so this overload's return type makes that guarantee structural
    /// rather than something a caller re-derives from <see cref="Transform"/>'s switch, which is the
    /// third instance of the <c>object?</c>-conflation shape this repo's CLAUDE.md already tracks twice
    /// (#490).
    /// </remarks>
    public static string? Sanitize(string? content, ICompositeResponseSanitizer sanitizer, string toolName) =>
        (string?)Sanitize((object?)content, sanitizer, toolName);

    /// <summary>
    /// Runs <paramref name="result"/>'s text through <paramref name="sanitizer"/> and then
    /// <paramref name="redactionFilter"/>, in that order, preserving shape exactly as <see cref="Sanitize(object?, ICompositeResponseSanitizer, string)"/>
    /// does. Used only by <see cref="DefaultToolClassificationGate.RedactResult(string, object?)"/> — the
    /// path a classification policy's <c>Redact</c> verdict takes, which must do strictly more than the
    /// baseline sanitize every other tool result already gets (#484), not the same thing under a
    /// different name.
    /// </summary>
    /// <remarks>
    /// Sanitize before redact, mirroring <see cref="Tools.ReportedFailureText.PrepareForReporting"/>'s
    /// own ordering rationale: an injection payload is stripped before the (now shorter, already-inert)
    /// text is scanned for secret patterns, rather than redacting first and handing the sanitizer text
    /// that may already contain <c>[REDACTED:...]</c> placeholders to no benefit.
    /// </remarks>
    public static object? SanitizeAndRedact(
        object? result,
        ICompositeResponseSanitizer sanitizer,
        IContentRedactionFilter redactionFilter,
        string toolName) =>
        Transform(result, text => redactionFilter.Redact(SanitizeText(text, sanitizer, toolName), RedactionCategories.All));

    /// <summary>
    /// String-typed overload of <see cref="SanitizeAndRedact(object?, ICompositeResponseSanitizer, IContentRedactionFilter, string)"/>
    /// for a caller that already knows its content is plain text — the <c>RedactResult(string, string?)</c>
    /// boundary <see cref="IToolClassificationGate"/> exposes for exactly this case.
    /// </summary>
    /// <remarks>
    /// <strong>Non-null input always produces non-null output.</strong> <see cref="IContentRedactionFilter.Redact"/>
    /// never returns null (its own contract: null/empty/no-match input is returned unchanged), so this
    /// overload carries the same guarantee as the sibling <see cref="Sanitize(string?, ICompositeResponseSanitizer, string)"/>
    /// overload — see its remarks for why that guarantee matters (#490).
    /// </remarks>
    public static string? SanitizeAndRedact(
        string? content,
        ICompositeResponseSanitizer sanitizer,
        IContentRedactionFilter redactionFilter,
        string toolName) =>
        (string?)SanitizeAndRedact((object?)content, sanitizer, redactionFilter, toolName);

    /// <summary>
    /// Cuts the free text carried by <paramref name="result"/> so that its <strong>total</strong>
    /// across every text-carrying block is at most <paramref name="ceiling"/> characters, preserving
    /// shape exactly as <see cref="Sanitize(object?, ICompositeResponseSanitizer, string)"/> does.
    /// </summary>
    /// <param name="result">The tool result to bound.</param>
    /// <param name="ceiling">Maximum total characters of free text, inclusive of the marker.</param>
    /// <param name="marker">Appended where the cut lands, so the cut is visible to the model.</param>
    /// <remarks>
    /// <para>
    /// <strong>The budget spans blocks; it is not applied per block.</strong> A multi-content-block
    /// result — what an MCP tool returns — would otherwise admit <c>ceiling x blockCount</c>
    /// characters, which bounds nothing on the shape that most needs bounding. Blocks are walked in
    /// order and each takes what remains; once the budget is spent, later blocks come back empty. The
    /// marker sits at the cut, so the model is told the output was truncated exactly once rather than
    /// once per block.
    /// </para>
    /// <para>
    /// Delegates to <see cref="BoundedText.Cap"/> rather than slicing, so this inherits the
    /// surrogate-pair guarantee every other trust-boundary truncation site in the repo relies on
    /// (#467/#470) — a cut that would land inside a surrogate pair backs off by one instead.
    /// </para>
    /// <para>
    /// Structured values are untouched for the same reason <see cref="Sanitize(object?, ICompositeResponseSanitizer, string)"/> leaves them alone:
    /// a serialized result's <c>structuredContent</c> is typed JSON, not free text, and cutting it
    /// mid-value produces something the model mis-parses rather than something it reads as truncated.
    /// Bounding a result whose size lives entirely in structured content is therefore out of scope
    /// here and belongs to whatever budgets a whole turn (#522).
    /// </para>
    /// </remarks>
    /// <returns>
    /// The (possibly cut) result, and whether anything was dropped — the caller's only signal that a
    /// truncation happened, since (unlike <see cref="PreCutForScan(object?, int, int, string)"/>'s
    /// caller) nothing else here reports it (#521: the pipeline's <c>object?</c>-shaped cut needed this
    /// to decide whether to spill the full text for later retrieval; before, it had no truncation
    /// signal of its own).
    /// </returns>
    public static (object? Result, bool Dropped) Bound(object? result, int ceiling, string marker) =>
        BudgetedCut(result, ceiling, marker);

    /// <summary>
    /// Cuts the free text carried by <paramref name="result"/> to a scan-cost-bounded region — the total
    /// budget across every text-carrying block is <paramref name="ceiling"/> plus
    /// <paramref name="overlapMargin"/> — before any sanitizer or redaction filter sees it, so a result
    /// far larger than <paramref name="ceiling"/> does not pay to be scanned in full to return a
    /// fraction (#487).
    /// </summary>
    /// <param name="result">The tool result to pre-cut.</param>
    /// <param name="ceiling">The ceiling the eventual <see cref="Bound"/> call will cut to.</param>
    /// <param name="overlapMargin">
    /// How much beyond <paramref name="ceiling"/> is kept while scanning, so a secret or an injection
    /// pattern straddling the ceiling is still inside the scanned region and is still caught by the
    /// sanitizer/redaction pass that follows, rather than being sliced in half and emitted. Removed
    /// again by the later <see cref="Bound"/> call.
    /// </param>
    /// <param name="marker">
    /// Appended where the pre-cut itself lands. Leave as the default empty string when the caller
    /// reports truncation through its own out-parameter instead (OR the returned <c>Dropped</c> flag
    /// into it) — the marker the model sees then comes from the later <see cref="Bound"/> call alone,
    /// and a second one here would be redundant noise inside the still-scanned region.
    /// <strong>A caller with no such out-parameter must pass a real marker</strong>, or a drop this
    /// method makes is invisible whenever the sanitize/redact pass that follows shrinks the surviving
    /// text back under <paramref name="ceiling"/> — the one case <see cref="Bound"/>'s own cut never
    /// fires to leave a marker of its own. This is what ties a prefix DIRECTLY to being reported as one,
    /// not left to a caller remembering to OR two booleans it does not have when it has no
    /// out-parameter to OR them into (see <see cref="Services.Tools.DirectToolInvoker"/>'s remarks on
    /// exactly this shape of miss).
    /// </param>
    /// <returns>The (possibly cut) result, and whether anything was dropped.</returns>
    /// <remarks>
    /// <para>
    /// The budget spans blocks, exactly as <see cref="Bound"/>'s does — a multi-content-block MCP
    /// result would otherwise admit <c>(ceiling + overlapMargin) x blockCount</c> characters of scan
    /// cost, which bounds nothing on the shape that most needs bounding.
    /// </para>
    /// <para>
    /// <strong>Known residual, stated rather than papered over.</strong> The pre-cut can bisect a match
    /// at the scan ceiling, leaving a prefix the sanitizer/redaction pass cannot match. That prefix
    /// normally sits beyond <paramref name="ceiling"/> and is discarded by the later <see cref="Bound"/>
    /// call — but sanitizing and redacting shrink text, so if net shrinkage across the scanned region
    /// exceeds <paramref name="overlapMargin"/>, the prefix can migrate below the ceiling and be
    /// returned. No cheap check distinguishes a migrated prefix from ordinary content; the honest
    /// mitigation is the margin being large relative to plausible shrinkage. Removing this pre-cut
    /// entirely means paying for a full sanitizer/redaction pass over an arbitrarily large tool result
    /// on paths a remote caller or a sandboxed tool can trigger — the trade this method exists to make.
    /// </para>
    /// </remarks>
    public static (object? Result, bool Dropped) PreCutForScan(
        object? result, int ceiling, int overlapMargin, string marker = "")
    {
        // Saturating rather than wrapping: the arithmetic should not depend on a check in another
        // assembly (the config validator bounds the ceiling) to stay correct on this path.
        var scanCeiling = ceiling <= int.MaxValue - overlapMargin ? ceiling + overlapMargin : int.MaxValue;
        return BudgetedCut(result, scanCeiling, marker);
    }

    /// <summary>
    /// The shared cross-block budget walk both <see cref="Bound"/> and <see cref="PreCutForScan(object?, int, int, string)"/>
    /// reduce to — the two differ only in what ceiling they pass in and whether they need
    /// <c>Dropped</c> back, not in how the walk itself works.
    /// </summary>
    private static (object? Result, bool Dropped) BudgetedCut(object? result, int ceiling, string marker)
    {
        // #565: ExtractText rejoins a multi-block result with Environment.NewLine between blocks, but
        // this per-block walk previously summed only raw block lengths against `ceiling` — under-
        // counting the true emitted length (what a caller actually reads via ExtractText) by up to
        // (blockCount - 1) * Environment.NewLine.Length for a multi-block result. Reserving that cost
        // up front, out of the SAME budget every block draws from, means the total INCLUDING separators
        // never exceeds `ceiling` — tightening this method's own "total across blocks is at most
        // ceiling" guarantee for every caller (Bound, PreCutForScan), not just the aggregate-budget
        // settlement that originally surfaced the gap. Math.Max floors at 0 rather than going negative
        // when the reserve alone would exceed the ceiling — every block then gets cut to nothing on
        // first touch, which is the correct degenerate answer for an unreasonably small ceiling.
        var remaining = Math.Max(0, ceiling - SeparatorReserve(result));
        var dropped = false;

        var transformed = Transform(result, text =>
        {
            if (text.Length <= remaining)
            {
                remaining -= text.Length;
                return text;
            }

            dropped = true;
            var (cut, _) = BoundedText.Cap(text, remaining, marker);
            remaining = 0;
            return cut;
        });

        return (transformed, dropped);
    }

    /// <summary>
    /// How many characters <see cref="ExtractText"/> will spend on <see cref="Environment.NewLine"/>
    /// separators when it later rejoins <paramref name="result"/>'s text-carrying blocks — zero for
    /// every shape with at most one such block, since a lone block has no neighbor to separate from.
    /// </summary>
    private static int SeparatorReserve(object? result)
    {
        var textBlockCount = result switch
        {
            AIContent[] blocks => blocks.Count(b => b is TextContent),
            JsonElement { ValueKind: JsonValueKind.Object } element when TryGetContentArray(element, out var content) =>
                CountTextCarryingBlocks(content),
            _ => 1
        };

        return textBlockCount > 1 ? (textBlockCount - 1) * Environment.NewLine.Length : 0;
    }

    /// <summary>
    /// Counts the blocks <see cref="ExtractContentArrayText"/> would actually join — the same
    /// <see cref="IsContentBlock"/>/<see cref="HasBlockText"/> recognition <see cref="TryGetBlockText"/>
    /// uses (via the shared <see cref="ResolveTextHolder"/>), so this count and that join can never
    /// disagree about how many separators will really be inserted.
    /// </summary>
    /// <remarks>
    /// Efficiency finding (/simplify): an earlier version called <see cref="TryGetBlockText"/> here and
    /// discarded its extracted text — decoding every block's string via <c>JsonElement.GetString()</c>
    /// just to count it, then <see cref="Transform"/>'s own walk (via
    /// <see cref="TransformSerializedContentBlocks"/>) decoded the SAME blocks again to actually use
    /// them. <see cref="HasBlockText"/> shares the identical structural recognition without extracting.
    /// </remarks>
    private static int CountTextCarryingBlocks(JsonElement content) =>
        content.EnumerateArray().Count(block => IsContentBlock(block, out var type) && HasBlockText(block, type));

    /// <summary>
    /// String-typed overload of <see cref="PreCutForScan(object?, int, int, string)"/> for a caller that
    /// already knows its content is plain text — see that overload's remarks for the pre-cut rationale
    /// and for when <paramref name="marker"/> must be non-empty. Carries the same non-null-in/non-null-out
    /// guarantee as <see cref="Sanitize(string?, ICompositeResponseSanitizer, string)"/>, for the
    /// identical reason: a <see langword="string"/>-typed input can only produce a
    /// <see langword="string"/>-typed output through <see cref="Transform"/>'s <c>case string</c> arm.
    /// </summary>
    public static (string? Text, bool Dropped) PreCutForScan(
        string? content, int ceiling, int overlapMargin, string marker = "")
    {
        var (transformed, dropped) = PreCutForScan((object?)content, ceiling, overlapMargin, marker);
        return ((string?)transformed, dropped);
    }

    /// <summary>
    /// Applies <paramref name="transform"/> to the free text carried by <paramref name="result"/>,
    /// preserving whichever of the five recognized shapes it arrived in, and returns
    /// <paramref name="result"/> itself — not a reconstructed equivalent — whenever the transform left
    /// every text value unchanged.
    /// </summary>
    private static object? Transform(object? result, Func<string, string> transform)
    {
        switch (result)
        {
            case string content:
            {
                var transformed = transform(content);
                return string.Equals(transformed, content, StringComparison.Ordinal) ? result : transformed;
            }
            case JsonElement { ValueKind: JsonValueKind.String } element:
            {
                var original = element.GetString() ?? string.Empty;
                var transformed = transform(original);
                return string.Equals(transformed, original, StringComparison.Ordinal)
                    ? result
                    : JsonSerializer.SerializeToElement(transformed);
            }
            // A single-content-block MCP tool success reaches this boundary as a bare TextContent, not a
            // JsonElement — McpClientTool.InvokeCoreAsync only falls back to serializing the whole
            // CallToolResult when structured content or protocol metadata is present.
            case TextContent text:
            {
                var transformed = transform(text.Text);
                return string.Equals(transformed, text.Text, StringComparison.Ordinal)
                    ? result
                    : WithText(text, transformed);
            }
            // A multi-content-block MCP tool success reaches this boundary as AIContent[]. TextContent
            // elements carry free text directly; a tool_result block (#552) converts to a
            // FunctionResultContent whose Result can itself be a TextContent carrying the originating
            // server's text verbatim — confirmed against the pinned ModelContextProtocol.Core 1.4.1 /
            // Microsoft.Extensions.AI.Abstractions 10.5.2 assemblies, not assumed. Anything else
            // (DataContent — images, files; a FunctionResultContent whose Result isn't a TextContent)
            // passes through untouched.
            case AIContent[] blocks:
            {
                AIContent[]? transformedBlocks = null;
                for (var i = 0; i < blocks.Length; i++)
                {
                    switch (blocks[i])
                    {
                        case TextContent block:
                        {
                            var transformed = transform(block.Text);
                            if (string.Equals(transformed, block.Text, StringComparison.Ordinal))
                                continue;

                            transformedBlocks ??= (AIContent[])blocks.Clone();
                            transformedBlocks[i] = WithText(block, transformed);
                            break;
                        }
                        case FunctionResultContent { Result: TextContent innerText } frc:
                        {
                            var transformed = transform(innerText.Text);
                            if (string.Equals(transformed, innerText.Text, StringComparison.Ordinal))
                                continue;

                            transformedBlocks ??= (AIContent[])blocks.Clone();
                            transformedBlocks[i] = WithFunctionResult(frc, WithText(innerText, transformed));
                            break;
                        }
                    }
                }
                return transformedBlocks ?? result;
            }
            // #483: an MCP tool success carrying structuredContent or protocol _meta serializes as the
            // whole CallToolResult rather than a bare TextContent/AIContent[] — but it still carries the
            // same content array of text/data blocks, one JSON level down. Detected structurally (a
            // top-level "content" array) rather than by referencing the MCP protocol's own CLR types:
            // this project deliberately has no dependency on ModelContextProtocol.Core (see the type
            // remarks), so the shape is recognized by what it looks like, not by decoding it as a
            // specific SDK type.
            case JsonElement { ValueKind: JsonValueKind.Object } element when TryGetContentArray(element, out var content):
            {
                var transformed = TransformSerializedContentBlocks(element, content, transform);
                return transformed ?? result;
            }
            default:
                return result;
        }
    }

    /// <summary>
    /// Reduces <paramref name="result"/>'s free text to one flat string, across the same shapes
    /// <see cref="Transform"/> recognizes — the extraction counterpart for a caller that needs plain
    /// text rather than a shape-preserving rewrite (the direct-invocation HTTP surface, which returns a
    /// flat string to its caller rather than replaying structured content back to a model). A
    /// multi-block <see cref="AIContent"/>[] or content array joins every text-carrying block with a
    /// newline, skipping non-text blocks (e.g. images) — there is no shape left to preserve them in once
    /// reduced to a single string.
    /// </summary>
    public static string ExtractText(object? result) => result switch
    {
        null => string.Empty,
        string text => text,
        JsonElement { ValueKind: JsonValueKind.String } element => element.GetString() ?? string.Empty,
        TextContent text => text.Text,
        AIContent[] blocks => string.Join(Environment.NewLine, blocks.OfType<TextContent>().Select(b => b.Text)),
        JsonElement { ValueKind: JsonValueKind.Object } element when TryGetContentArray(element, out var content) =>
            ExtractContentArrayText(content),
        JsonElement element => element.GetRawText(),
        _ => JsonSerializer.Serialize(result)
    };

    /// <summary>
    /// Joins every text-carrying block's text (plain <c>text</c> blocks, embedded <c>resource</c>
    /// blocks, and a <c>tool_result</c> block's own nested text — one level, see
    /// <see cref="JoinTextCarryingBlocks"/>) with a newline, skipping blocks with nothing to extract
    /// (e.g. a binary <c>resource</c> or image block).
    /// </summary>
    private static string ExtractContentArrayText(JsonElement content) =>
        JoinTextCarryingBlocks(content, allowNestedToolResult: true);

    /// <summary>
    /// The shared walk <see cref="ExtractContentArrayText"/> reduces to, and also what a
    /// <c>tool_result</c> block's own nested <c>content</c> array is joined with (#552) — a
    /// <c>tool_result</c> block (<c>ToolResultContentBlock</c> on the wire) carries its own nested
    /// content array one JSON level down, structurally identical to the top-level one, and an MCP
    /// server picking that shape over a bare <c>text</c> block must not skip extraction by doing so.
    /// </summary>
    /// <param name="allowNestedToolResult">
    /// <see langword="false"/> on the recursive call for a <c>tool_result</c>'s own nested array, so a
    /// <c>tool_result</c> block nested inside another <c>tool_result</c> block is not itself unwrapped.
    /// The real protocol never nests <c>tool_result</c> this way (confirmed against the pinned
    /// <c>ModelContextProtocol.Core</c> 1.4.1 assembly: <c>ToolResultContentBlock.Content</c> is
    /// <c>IList&lt;ContentBlock&gt;</c>, never a list that could itself hold another
    /// <c>ToolResultContentBlock</c> two levels deep in any flow this repo produces) — recursing
    /// without this bound would trade a fixed, one-level gap for an unbounded one: a hostile server can
    /// still wire-craft arbitrarily deep JSON nesting even though the real SDK types cannot represent
    /// it, and walking that without a depth bound is a stack-depth denial-of-service on attacker-
    /// controlled input. This flag statically bounds the walk to exactly one recursive call: the value
    /// passed on the only recursive call site is a literal <see langword="false"/>, never re-derived
    /// from the caller's own flag, so a third level cannot be introduced without visibly changing that
    /// literal.
    /// </param>
    private static string JoinTextCarryingBlocks(JsonElement content, bool allowNestedToolResult)
    {
        List<string>? texts = null;
        foreach (var block in content.EnumerateArray())
        {
            if (!IsContentBlock(block, out var type))
                continue;

            if (TryGetBlockText(block, type, out var text, out _))
            {
                (texts ??= []).Add(text);
            }
            else if (allowNestedToolResult && TryGetNestedToolResultContent(type, block, out var nested))
            {
                var nestedText = JoinTextCarryingBlocks(nested, allowNestedToolResult: false);
                if (nestedText.Length > 0)
                    (texts ??= []).Add(nestedText);
            }
        }
        return texts is null ? string.Empty : string.Join(Environment.NewLine, texts);
    }

    /// <summary>
    /// Whether <paramref name="block"/> is a <c>tool_result</c> block carrying its own nested
    /// <c>content</c> array — the one shape every <c>tool_result</c>-aware call site in this file
    /// checks for, kept as one structural check for the same reason <see cref="IsContentBlock"/> is.
    /// </summary>
    private static bool TryGetNestedToolResultContent(string? type, JsonElement block, out JsonElement nested)
    {
        if (type == "tool_result" && block.TryGetProperty("content", out nested) && nested.ValueKind == JsonValueKind.Array)
            return true;

        nested = default;
        return false;
    }

    /// <summary>
    /// Recognizes the MCP wire shape's top-level <c>content</c> array by structure — shared with
    /// <see cref="Tools.McpFailureNormalizingAIFunction"/>, which recognizes the same array to find a
    /// failure's text rather than to rewrite one. Kept as one structural check rather than two so the
    /// "what does an MCP content array look like" knowledge can't drift between the two call sites.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A bare top-level <c>content</c> array is not unique to MCP — a keyed-DI tool's own domain JSON
    /// schema could coincidentally use that same property name, and would previously have been
    /// misidentified and silently reparsed as an MCP result (#488).
    /// </para>
    /// <para>
    /// Recognizes an array containing <em>at least one</em> element shaped like a real MCP content
    /// block — an object with a string <c>type</c> property, the one thing every content-block kind
    /// shares, checked structurally rather than against the specific kinds MCP defines today: a
    /// hardcoded list goes stale the moment the protocol adds one, and staleness here means silently
    /// un-recognizing a real MCP result, not just tolerating one more first-party coincidence.
    /// </para>
    /// <para>
    /// This is the third design considered. A marker-presence check
    /// (<c>isError</c>/<c>structuredContent</c>/<c>_meta</c>) and a require-<em>every</em>-element version
    /// were each tried and reverted after independent security review found a false negative in each —
    /// see this repo's CLAUDE.md Common Mistakes (#488) for the full history before changing this method
    /// again, so a fourth round doesn't reintroduce either regression.
    /// </para>
    /// </remarks>
    internal static bool TryGetContentArray(JsonElement element, out JsonElement content)
    {
        if (!element.TryGetProperty("content", out content) || content.ValueKind != JsonValueKind.Array)
            return false;

        // A foreach + early return, not .Any(...) — JsonElement.ArrayEnumerator is a struct, and LINQ's
        // IEnumerable<T>-typed Any() would box it. Still short-circuits on the first qualifying block —
        // the common case for a genuine MCP result, where a real block is typically among the first
        // elements, not a full second pass over the array every recognized call would otherwise pay for
        // on top of the processing pass.
        foreach (var block in content.EnumerateArray())
        {
            if (IsContentBlock(block, out _))
                return true;
        }

        // An empty content array (Content.Count == 0's fall-through, e.g. AIFunctionMcpServerTool's own
        // null => branch) has nothing to distinguish it from a first-party empty array by shape alone —
        // there is also nothing in it to sanitize, redact, or extract, so recognizing it costs nothing.
        return content.GetArrayLength() == 0;
    }

    /// <summary>
    /// Whether <paramref name="block"/> has the one shape every MCP content block shares — an object
    /// with a required string <c>type</c> discriminator — shared by every place in this file that walks
    /// a content array, so "what counts as a content block" can't drift between them (#488 second
    /// review round: it had, three separate hand-written copies of this exact check).
    /// </summary>
    /// <remarks>
    /// <see langword="internal"/> rather than <see langword="private"/> so
    /// <see cref="Tools.McpFailureNormalizingAIFunction"/>'s own content-array walk shares this same
    /// predicate instead of re-deriving it (#554) — that walk had drifted into a fourth independent
    /// copy of "what counts as a block" before this fix, missing the <c>type</c> requirement entirely.
    /// </remarks>
    internal static bool IsContentBlock(JsonElement block, out string? type)
    {
        if (block.ValueKind == JsonValueKind.Object
            && block.TryGetProperty("type", out var typeProp)
            && typeProp.ValueKind == JsonValueKind.String)
        {
            type = typeProp.GetString();
            return true;
        }

        type = null;
        return false;
    }

    /// <summary>
    /// Walks the <c>content</c> array of a serialized <c>CallToolResult</c>, applying
    /// <paramref name="transform"/> to every block that carries free text: a <c>{"type":"text",
    /// "text":"..."}</c> block, and a <c>{"type":"resource","resource":{"text":"...",...}}</c> embedded
    /// text resource — confirmed against <c>ModelContextProtocol.Core</c>'s content-block union
    /// (<c>EmbeddedResourceBlock</c>/<c>TextResourceContents</c>): the SDK converts both shapes to
    /// model-visible text on the <c>AIContent[]</c> path, so both must be scrubbed here too, or an MCP
    /// server picks which shape to answer with and skips the pass by choosing <c>resource</c> (#483's
    /// original text-only handling was a security review finding on the PR that added it). A <c>resource</c>
    /// block backing a binary blob (no <c>text</c> property) has nothing to transform and passes through.
    /// Every other property (<c>isError</c>, <c>structuredContent</c>, <c>_meta</c>, non-text-carrying
    /// blocks) is carried through unchanged — <c>structuredContent</c> is typed JSON, not free text, and
    /// rewriting it risks producing a malformed result the model then mis-parses (tracked separately,
    /// see <see cref="IToolClassificationGate.RedactResult(string, object?)"/>'s remarks on the Redact verdict's coverage
    /// there). Returns <see langword="null"/> when no block's content changed, so the caller can keep
    /// the original <see cref="JsonElement"/> instead of an equivalent reconstruction.
    /// </summary>
    private static JsonElement? TransformSerializedContentBlocks(
        JsonElement original, JsonElement content, Func<string, string> transform)
    {
        // Parsed lazily, only once a block anywhere (including inside a tool_result's own nested
        // array) actually needs rewriting: the common case (a structured result with nothing to
        // scrub) pays no JsonNode allocation at all.
        JsonNode? root = null;
        JsonNode GetTopArray() => (root ??= JsonNode.Parse(original.GetRawText()))!["content"]!;

        TransformBlocks(content, transform, GetTopArray, allowNestedToolResult: true);

        return root is null ? null : JsonSerializer.SerializeToElement(root);
    }

    /// <summary>
    /// The shared walk-and-mutate <see cref="TransformSerializedContentBlocks"/> reduces to, called
    /// once for the top-level <c>content</c> array and once more, explicitly, for a <c>tool_result</c>
    /// block's own nested one (#552) — never recursively beyond that, for the same reason
    /// <see cref="JoinTextCarryingBlocks"/>'s <paramref name="allowNestedToolResult"/> is bounded the
    /// same way; see that method's remarks.
    /// </summary>
    /// <param name="getArrayNode">
    /// Resolves the mutable <see cref="JsonNode"/> array mirroring <paramref name="content"/> —
    /// <c>root["content"]</c> for the top-level call, or (for the recursive one-level call) a closure
    /// that re-derives <c>root["content"][toolResultIndex]["content"]</c> each time, so the lazy
    /// <c>root</c> parse still happens at most once no matter which level's block needs rewriting.
    /// </param>
    private static void TransformBlocks(
        JsonElement content, Func<string, string> transform, Func<JsonNode> getArrayNode, bool allowNestedToolResult)
    {
        var index = 0;

        foreach (var block in content.EnumerateArray())
        {
            var blockIndex = index; // captured per-iteration; a shared loop variable would alias every closure below
            if (IsContentBlock(block, out var type))
            {
                if (TryGetBlockText(block, type, out var text, out var isEmbeddedResource))
                {
                    var transformed = transform(text);
                    if (!string.Equals(transformed, text, StringComparison.Ordinal))
                    {
                        var target = isEmbeddedResource
                            ? getArrayNode()[blockIndex]!["resource"]
                            : getArrayNode()[blockIndex];
                        target!["text"] = transformed;
                    }
                }
                else if (allowNestedToolResult && TryGetNestedToolResultContent(type, block, out var nested))
                {
                    TransformBlocks(
                        nested, transform, () => getArrayNode()[blockIndex]!["content"]!,
                        allowNestedToolResult: false);
                }
            }

            index++;
        }
    }

    /// <summary>
    /// Extracts the free text a content block carries, if any: a plain <c>"text"</c> block's own
    /// <c>text</c> property, or a <c>"resource"</c> block's nested <c>resource.text</c> (a
    /// <c>TextResourceContents</c> — a <c>BlobResourceContents</c> has no <c>text</c> property and
    /// correctly answers <see langword="false"/> here, since there is nothing to sanitize).
    /// </summary>
    private static bool TryGetBlockText(JsonElement block, string? type, out string text, out bool isEmbeddedResource)
    {
        isEmbeddedResource = type == "resource";
        if (ResolveTextHolder(block, type) is { } holder
            && holder.TryGetProperty("text", out var textProp)
            && textProp.ValueKind == JsonValueKind.String)
        {
            text = textProp.GetString() ?? string.Empty;
            return true;
        }

        text = string.Empty;
        return false;
    }

    /// <summary>
    /// Whether a content block carries free text, without extracting or decoding it — the count-only
    /// sibling of <see cref="TryGetBlockText"/>/<see cref="JoinTextCarryingBlocks"/>, sharing the
    /// identical <see cref="ResolveTextHolder"/>/<see cref="TryGetNestedToolResultContent"/>
    /// recognition (including the same one-level <c>tool_result</c> bound, #552) so none of the three
    /// can disagree about which blocks qualify.
    /// </summary>
    private static bool HasBlockText(JsonElement block, string? type, bool allowNestedToolResult = true)
    {
        if (ResolveTextHolder(block, type) is { } holder
            && holder.TryGetProperty("text", out var textProp)
            && textProp.ValueKind == JsonValueKind.String)
            return true;

        return allowNestedToolResult
            && TryGetNestedToolResultContent(type, block, out var nested)
            && nested.EnumerateArray().Any(b => IsContentBlock(b, out var nestedType)
                && HasBlockText(b, nestedType, allowNestedToolResult: false));
    }

    /// <summary>
    /// Resolves the JSON object a content block's free text would live under: the block itself for a
    /// plain <c>"text"</c> block, or its nested <c>resource</c> object for an embedded-resource block —
    /// or <see langword="null"/> when neither shape matches, or the resource holder is missing/malformed.
    /// </summary>
    private static JsonElement? ResolveTextHolder(JsonElement block, string? type) => type switch
    {
        "text" => block,
        "resource" when block.TryGetProperty("resource", out var resource)
            && resource.ValueKind == JsonValueKind.Object => resource,
        _ => null
    };

    private static string SanitizeText(string text, ICompositeResponseSanitizer sanitizer, string toolName)
    {
        var scrubbed = sanitizer.Sanitize(text, toolName);
        return scrubbed.WasSanitized ? SanitizedText(scrubbed) : text;
    }

    private static string SanitizedText(SanitizationResult result) =>
        result.SanitizedContent ?? CorruptedSanitizerOutputPlaceholder;

    private static TextContent WithText(TextContent original, string text) => new(text)
    {
        Annotations = original.Annotations,
        RawRepresentation = original.RawRepresentation,
        AdditionalProperties = original.AdditionalProperties
    };

    /// <summary>
    /// Rebuilds a <see cref="FunctionResultContent"/> around a new <see cref="FunctionResultContent.Result"/>,
    /// preserving <see cref="ToolResultContent.CallId"/> — load-bearing for a live caller/result
    /// correlation elsewhere in this repo (see #556) — plus <see cref="FunctionResultContent.Exception"/>.
    /// </summary>
    private static FunctionResultContent WithFunctionResult(FunctionResultContent original, object? result) => new(
        original.CallId, result)
    {
        Exception = original.Exception,
        Annotations = original.Annotations,
        RawRepresentation = original.RawRepresentation,
        AdditionalProperties = original.AdditionalProperties
    };
}
