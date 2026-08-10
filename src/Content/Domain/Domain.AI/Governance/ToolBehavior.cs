namespace Domain.AI.Governance;

/// <summary>
/// Where a tool's declared behaviour came from, which decides how much weight it may carry.
/// </summary>
/// <remarks>
/// <para>
/// The source matters because a behaviour declaration is an <em>input to a security decision that the
/// declarer supplies</em>. The MCP specification says so directly: annotations "are not guaranteed to
/// provide a faithful description of tool behavior", and clients "should never make tool use decisions
/// based on <c>ToolAnnotations</c> received from untrusted servers". A server that wants to escape an
/// approval gate simply marks its destructive tool read-only.
/// </para>
/// <para>
/// The rule this enum exists to support: <strong>a declaration is believed when it tightens, and only
/// from a trusted source when it loosens.</strong> Nobody lies in the strict direction, so
/// <see cref="ToolBehavior.Destructive"/> can be taken at face value from anyone; only
/// <see cref="ToolBehavior.ReadOnly"/> — the one that removes friction — needs provenance.
/// </para>
/// </remarks>
public enum ToolBehaviorSource
{
    /// <summary>
    /// Nothing is known about the tool's behaviour. The fail-closed case: an unknown tool is treated as
    /// one that writes, exactly as an unknown knowledge scope is treated as global rather than private.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// The tool is registered in this process and declared its own behaviour through
    /// <c>ITool.IsReadOnly</c>. Authored in the host's own codebase, so its declaration is as
    /// trustworthy as the rest of that codebase.
    /// </summary>
    FirstParty = 1,

    /// <summary>
    /// The tool came from an external MCP server the operator has explicitly marked as trusted via
    /// <c>McpServerDefinition.TrustToolAnnotations</c>. Its read-only claim may exempt it from approval.
    /// </summary>
    TrustedMcpServer = 2,

    /// <summary>
    /// The tool came from an external MCP server carrying no such marking — the default for every
    /// configured server. Its claims are recorded and reported, and may tighten the outcome, but a
    /// read-only claim does <strong>not</strong> exempt it from approval.
    /// </summary>
    UntrustedMcpServer = 3,
}

/// <summary>
/// What a tool has declared about its own behaviour, together with who declared it.
/// </summary>
/// <remarks>
/// <para>
/// The four hints mirror the MCP tool annotations exactly, including their nullability, because the
/// distinction between "declared false" and "did not say" is the whole point. A tool that says nothing
/// is not a tool that says it is safe.
/// </para>
/// <para>
/// The per-hint defaults below are the MCP specification's own, and they are all fail-closed:
/// unspecified read-only means "assume it writes"; unspecified destructive means "assume it can
/// destroy"; unspecified open-world means "assume it reaches an unpredictable set of entities".
/// This type deliberately stores the raw <see langword="null"/> rather than folding the default in, so
/// that a report can distinguish a server that thought about the question from one that did not.
/// </para>
/// <para>
/// <strong>The destructive default is conditional, and <see cref="IsExemptFromApproval"/> is
/// deliberately not written as though it were absolute.</strong> The specification scopes
/// <c>destructiveHint</c> to tools that modify their environment — it is the question "additively, or
/// destructively?", which only arises once the answer to "does it write at all?" is yes. So an
/// unspecified destructive hint sitting beside <c>readOnlyHint: true</c> is not a fail-closed
/// "assume it destroys"; it is a question that was never in scope. Reading it the other way would
/// make every correctly-annotated read-only tool non-exempt and the whole posture unusable.
/// </para>
/// </remarks>
/// <param name="Source">Who declared this behaviour, and therefore how far it may be believed.</param>
/// <param name="ReadOnly">
/// Whether the tool only reads and never changes state. <see langword="null"/> when unspecified, which
/// the specification says to read as "not read-only".
/// </param>
/// <param name="Destructive">
/// Whether the tool can perform destructive rather than merely additive updates. <see langword="null"/>
/// when unspecified, which the specification says to read as "destructive".
/// </param>
/// <param name="Idempotent">
/// Whether repeating the call with the same arguments has no further effect. <see langword="null"/>
/// when unspecified, which the specification says to read as "not idempotent".
/// </param>
/// <param name="OpenWorld">
/// Whether the tool interacts with an open, unpredictable set of external entities (a web search)
/// rather than a closed and well-defined one (a memory lookup). <see langword="null"/> when
/// unspecified, which the specification says to read as "open world".
/// </param>
/// <param name="ServerName">
/// The MCP server that advertised this tool, or <see langword="null"/> for a tool registered in this
/// process. Carried because a tool <em>name</em> is claimable by any configured server, so anything
/// keyed on the name alone — an operator's exemption, most of all — needs to know which party the
/// declaration it is acting on actually came from.
/// </param>
public sealed record ToolBehavior(
    ToolBehaviorSource Source,
    bool? ReadOnly = null,
    bool? Destructive = null,
    bool? Idempotent = null,
    bool? OpenWorld = null,
    string? ServerName = null)
{
    /// <summary>
    /// The behaviour of a tool nothing is known about: no declaration, no provenance, and therefore
    /// never exempt from approval.
    /// </summary>
    public static ToolBehavior Unknown { get; } = new(ToolBehaviorSource.Unknown);

    /// <summary>
    /// Whether this declaration is strong enough to let the tool skip human approval under the
    /// non-read-only approval posture.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two conditions, both required. The tool must have <em>said</em> it is read-only — silence is not
    /// a claim — and the claim must come from somewhere entitled to make it: this process's own code, or
    /// an MCP server the operator marked trusted. A read-only claim from an unmarked server is recorded
    /// and reported but buys nothing, which is precisely the case the MCP specification warns about.
    /// </para>
    /// <para>
    /// <strong>A destructive claim overrides a read-only one.</strong> The combination is incoherent —
    /// nothing that only reads can destroy — and an incoherent declaration is a reason to distrust the
    /// declarer, not to pick the half that is more convenient. This is the tightening direction, so it
    /// applies regardless of source.
    /// </para>
    /// </remarks>
    public bool IsExemptFromApproval => ReadOnly == true && Destructive != true && IsVouchedFor;

    /// <summary>
    /// Whether this declaration came from a party the operator has accepted as speaking truthfully
    /// about tool behaviour: the host's own code, or an MCP server explicitly marked trusted.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="IsExemptFromApproval"/> because provenance and the claim itself are
    /// different questions, and a caller that overrides the claim — an operator's exemption — must
    /// still be able to ask about the provenance. Folding the two together is what let a name-matched
    /// exemption apply to a declaration from a server nobody had vouched for.
    /// </remarks>
    public bool IsVouchedFor =>
        Source is ToolBehaviorSource.FirstParty or ToolBehaviorSource.TrustedMcpServer;

    /// <summary>
    /// A short operator-facing phrase explaining why this tool is not exempt, for the approval request
    /// a human actually reads. Returns <see langword="null"/> when the tool is exempt.
    /// </summary>
    /// <remarks>
    /// The three cases are genuinely different actions for whoever is paged: an undeclared tool needs
    /// someone to find out what it does, a self-declared-destructive tool needs a judgement call, and a
    /// read-only tool from an unmarked server needs a one-line configuration change if the server is in
    /// fact trusted. Collapsing them into "not read-only" would hide which one is happening.
    /// </remarks>
    public string? NonExemptReason => this switch
    {
        { IsExemptFromApproval: true } => null,
        { Destructive: true } => "the tool declares itself destructive",
        { ReadOnly: true, Source: ToolBehaviorSource.UntrustedMcpServer } =>
            "the tool claims to be read-only, but its MCP server is not marked as trusted for tool annotations",
        { Source: ToolBehaviorSource.Unknown } => "nothing is known about what the tool does",
        _ => "the tool has not declared itself read-only",
    };
}
