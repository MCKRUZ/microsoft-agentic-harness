namespace Application.AI.Common.Services.Governance;

/// <summary>
/// One logical tool's name-forms: every name a permission rule must cover for it, plus the published
/// name a caller actually invokes it by.
/// </summary>
/// <param name="PublishedName">
/// The tool's self-reported name, or the name itself when it names no first-party tool (an MCP tool,
/// a glob) or when key and published name already agree.
/// </param>
/// <param name="Forms">
/// The names to emit rules for — one entry normally, two when a first-party tool's DI registration
/// key and published name disagree, and possibly one even then (see <paramref name="Diverges"/>).
/// Never empty.
/// </param>
/// <param name="Diverges">
/// Whether this name resolved to a first-party tool whose published name disagrees with it.
/// </param>
public sealed record ToolNameForms(string PublishedName, IReadOnlyList<string> Forms, bool Diverges)
{
    /// <summary>
    /// The value to record as a rule's <c>PublishedToolName</c> for these forms: the published name
    /// when this name is one of several covering a single tool, and null for an ordinary name that
    /// needs no grouping.
    /// </summary>
    public string? GroupingName => GroupingNameFor(Diverges, PublishedName);

    /// <summary>
    /// The name-forms for <paramref name="name"/>, given what resolving it as a first-party tool returned.
    /// The one place that decides whether a resolved published name diverges from the name it was
    /// resolved from (#657): the envelope grant resolver and the plugin rule provider each used to make
    /// that comparison themselves, and a fix to one would have left the other standing.
    /// </summary>
    /// <remarks>
    /// Case-insensitive, matching how the runtime permission resolver's <c>GlobPatternMatcher</c> compares
    /// a rule pattern with an invoked name: a published name that differs only in case is the same tool
    /// and needs no second rule.
    /// </remarks>
    /// <param name="name">The declared, granted or denied name.</param>
    /// <param name="resolved">Whether <paramref name="name"/> resolved to a first-party tool.</param>
    /// <param name="publishedName">The resolved published name; ignored when <paramref name="resolved"/> is false.</param>
    /// <returns>One form normally; two when a first-party tool's key and published name disagree.</returns>
    public static ToolNameForms For(string name, bool resolved, string publishedName)
    {
        var diverges = resolved && !string.Equals(publishedName, name, StringComparison.OrdinalIgnoreCase);

        // PublishedName is the name the agent actually invokes the tool by, which is what a summary should
        // show; when nothing diverges it is simply the name itself.
        return diverges
            ? new ToolNameForms(publishedName, [name, publishedName], Diverges: true)
            : new ToolNameForms(name, [name], Diverges: false);
    }

    /// <summary>
    /// The single definition of when emitted rules are alternate names for one tool, for a caller
    /// that resolved the published name itself and has no <see cref="ToolNameForms"/> to hand.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Shared rather than re-derived per provider (/simplify finding): #652 was itself a mis-keyed
    /// grouping decision, so two independent definitions of "is this rule one of a pair" is exactly
    /// the shape that lets a fix in one provider leave the defect standing in the other.
    /// </para>
    /// <para>
    /// Keys off divergence, never off how many forms survived deduplication (code-review finding):
    /// dedup in <see cref="CapabilityEnvelopeGrantResolver.ExpandToNameForms"/> is global across the
    /// whole input, so a divergent name whose published form was already contributed by an earlier
    /// entry arrives holding a single form. Keying on the count left that rule untagged and the
    /// summary printed the DI key as its own line — the #652 defect, surviving in the fix for it.
    /// </para>
    /// </remarks>
    /// <param name="diverges">Whether the tool's published name disagrees with the name being covered.</param>
    /// <param name="publishedName">The tool's self-reported name.</param>
    public static string? GroupingNameFor(bool diverges, string publishedName) =>
        diverges ? publishedName : null;
}
