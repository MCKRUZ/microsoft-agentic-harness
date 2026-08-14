namespace Domain.Common.Config.AI.Governance;

/// <summary>
/// What a tool can do with the data that flows through it, for the purpose of detecting a dangerous
/// <em>combination</em> of tools rather than judging any one tool alone.
/// </summary>
/// <remarks>
/// <para>
/// Lives in <c>Domain.Common</c> rather than <c>Domain.AI</c>, alongside <see cref="ThreatLevel"/>,
/// because <see cref="Domain.Common.Config.AI.Governance.ToolCompositionGatingConfig"/> — a
/// <c>Domain.Common</c> config type — must be able to reference it, and <c>Domain.Common</c> cannot
/// depend on <c>Domain.AI</c> (the dependency runs the other way). The richer domain types that use
/// this enum — <c>Domain.AI.Governance.ToolCapabilityProfile</c>, <c>ToolCompositionFinding</c>,
/// <c>ToolCompositionTaint</c> — live in <c>Domain.AI</c> and reference this from there, exactly as
/// <c>GovernanceConfig</c>'s other enum-typed fields already do.
/// </para>
/// <para>
/// <strong>This is a direction-of-data-flow vocabulary, not a mutability vocabulary.</strong>
/// <c>ToolBehavior</c> (the four MCP hints — read-only, destructive, idempotent, open-world) answers
/// "how much can this one tool change?". This answers a different question: "can this tool bring
/// attacker-controlled content into the conversation, or can it act on that content in a way that
/// costs something?" A tool that only reads can still be the source half of an exfiltration pair — a
/// web-search tool is read-only and is exactly the shape of tool this vocabulary must flag as a
/// source.
/// </para>
/// <para>
/// Two source bits, three sink bits. <see cref="IngestsUntrustedInput"/> and
/// <see cref="ReadsCredentials"/> bring content into the agent's context that the agent did not
/// author and, in the first case, that an attacker may have authored on purpose.
/// <see cref="WritesFiles"/>, <see cref="ExecutesCode"/>, and <see cref="SendsOutbound"/> are where
/// that content can do damage once it has been read. The composition check
/// (<c>IToolCompositionAnalyzer</c>) looks for an agent holding at least one bit from each group.
/// </para>
/// <para>
/// <strong>Flags, because a tool can genuinely be both.</strong> A browser-automation tool that reads
/// a page and can also submit a form is a source and a sink in the same tool. The composition check
/// deliberately excludes self-pairs (see <c>ToolCompositionAnalyzer</c>'s remarks) — that case is
/// #324's job, since gating a single tool for what it alone can do is exactly what behaviour gating
/// already covers.
/// </para>
/// <para>
/// <strong>The vocabulary is deliberately small.</strong> A wider one — anything that touches
/// "read" or "write" in general — destroys the signal this check exists to produce: nearly every
/// real tool reads or writes something, so a wide vocabulary flags nearly every agent and the finding
/// stops meaning anything. See <c>ToolCapabilityKeywordRules</c> for the exact, narrow rule set and
/// the much larger list of tokens deliberately left out of it.
/// </para>
/// </remarks>
[Flags]
public enum ToolCompositionCapability
{
    /// <summary>No known capability. Not the same as "known to have no capability" — see
    /// <c>ToolCapabilityOrigin.Unclassified</c>, which this value is paired with when nothing could
    /// classify the tool at all.</summary>
    None = 0,

    /// <summary>
    /// The tool can bring content into the conversation that the agent did not author and does not
    /// control — a fetched web page, an inbound email, a search result, a downloaded file. The
    /// canonical injection carrier: an attacker who controls the source controls what the agent reads.
    /// </summary>
    IngestsUntrustedInput = 1,

    /// <summary>
    /// The tool can read secrets, credentials, or access tokens. Distinct from
    /// <see cref="IngestsUntrustedInput"/> because the risk here is not that the content is
    /// attacker-authored, but that it is sensitive — the composition risk is exfiltrating it, not
    /// being instructed by it.
    /// </summary>
    ReadsCredentials = 2,

    /// <summary>The tool can write, modify, or delete files or other persistent state.</summary>
    WritesFiles = 4,

    /// <summary>The tool can execute arbitrary code, shell commands, or scripts.</summary>
    ExecutesCode = 8,

    /// <summary>
    /// The tool can send data outside the current process or trust boundary — email, webhooks, chat
    /// messages, HTTP POSTs to an arbitrary or attacker-influenced destination. The canonical
    /// exfiltration sink.
    /// </summary>
    SendsOutbound = 16,
}

/// <summary>
/// The membership split of <see cref="ToolCompositionCapability"/> into source and sink bits — the one
/// place that grouping is written. Referenced by <c>ToolCompositionAnalyzer</c> (which bits to look for
/// on each side of a pairing) and <c>GovernanceConfigValidator</c> (which bits a
/// <see cref="ToolCompositionPairing.Source"/>/<see cref="ToolCompositionPairing.Sink"/> may legally
/// name). Adding a sixth capability bit means deciding its side once, here.
/// </summary>
public static class ToolCompositionCapabilities
{
    /// <summary>Bits that bring untrusted or sensitive content into the agent's context.</summary>
    public static readonly IReadOnlyList<ToolCompositionCapability> SourceBits =
        [ToolCompositionCapability.IngestsUntrustedInput, ToolCompositionCapability.ReadsCredentials];

    /// <summary>Bits that can act on that content in a way that costs something.</summary>
    public static readonly IReadOnlyList<ToolCompositionCapability> SinkBits =
        [ToolCompositionCapability.WritesFiles, ToolCompositionCapability.ExecutesCode, ToolCompositionCapability.SendsOutbound];
}

/// <summary>
/// The posture applied to one (source capability, sink capability) pairing. Bound per pairing from
/// <see cref="ToolCompositionGatingConfig.Pairings"/>, always resolved <strong>live</strong> against
/// the current config — never frozen into a <c>Domain.AI.Governance.ToolCompositionFinding</c>. See
/// that type's remarks for why: a posture baked in at agent build time cannot honour a config change
/// without an agent rebuild.
/// </summary>
public enum CompositionPosture
{
    /// <summary>The pairing is permitted. No report is emitted, and enforcement never engages.
    /// Zero by construction, so an unconfigured pairing — the default for every pairing on a host that
    /// has set nothing — is inert.</summary>
    Allow = 0,

    /// <summary>The pairing is reported (audit, metrics, structured log) but the sink tool is not
    /// additionally gated at call time.</summary>
    Warn = 1,

    /// <summary>The pairing is reported, and the sink tool additionally requires human approval at
    /// call time — folded into the same single approval question #324's behaviour posture already
    /// asks, never a second one. See <c>ToolInvocationGovernor.RequiresApprovalForToolComposition</c>.</summary>
    RequireApproval = 2,
}
