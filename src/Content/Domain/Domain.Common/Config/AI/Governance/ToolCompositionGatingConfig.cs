namespace Domain.Common.Config.AI.Governance;

/// <summary>
/// Governs tool <em>combinations</em> rather than individual tools: an agent holding both a tool that
/// can ingest untrusted or sensitive content and a tool that can act on it in a costly way is an
/// indirect-prompt-injection exfiltration primitive, even though every per-tool permission check
/// passes. Bound from <c>AppConfig:AI:Governance:ToolCompositionGating</c> in appsettings.json.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What this adds that #324 (behaviour gating) does not.</strong> Every existing tool
/// governance control — allow/deny lists, per-agent authorization, behaviour gating — asks "may this
/// agent call this tool?" one tool at a time. None asks what this does: "what can this agent do with
/// these tools <em>together</em>?" A tool that fetches a web page is not dangerous. A tool that sends
/// email is not dangerous. An agent holding both is an exfiltration primitive.
/// </para>
/// <para>
/// <strong>Off by default, and inert by construction rather than by a switch.</strong> Unlike
/// <see cref="ToolBehaviorGatingConfig"/>, this config carries no boolean "enabled" flag. Its
/// <see cref="DefaultPosture"/> is <see cref="CompositionPosture.Allow"/> and <see cref="Pairings"/>
/// starts empty, so a host that sets nothing here has every pairing resolve to Allow — no findings, no
/// enforcement. That is deliberate: the acceptance test for this feature is that the default
/// configuration is inert, and a config shape with nothing to "turn off" cannot regress into looking
/// off while quietly being on.
/// </para>
/// <para>
/// <strong>Enforcement needs company, exactly like #324.</strong> A pairing configured as
/// <see cref="CompositionPosture.RequireApproval"/> is applied inside the same tool governor #324's
/// posture lives in, so it needs <see cref="GovernanceConfig.EnforceToolInvocation"/> for the same
/// reason: the governor arms on enforcement or on a bundle run's capability envelope, and leaving
/// enforcement off would apply the posture to bundle runs alone. <c>GovernanceConfigValidator</c>
/// rejects that combination at startup.
/// </para>
/// </remarks>
public sealed class ToolCompositionGatingConfig
{
    /// <summary>
    /// The posture applied to a (source capability, sink capability) pairing that has no explicit entry
    /// in <see cref="Pairings"/>. <see cref="CompositionPosture.Allow"/> by default, so an unconfigured
    /// host reports and enforces nothing.
    /// </summary>
    public CompositionPosture DefaultPosture { get; init; } = CompositionPosture.Allow;

    /// <summary>
    /// The posture for specific (source capability, sink capability) pairings, overriding
    /// <see cref="DefaultPosture"/> for that pairing only.
    /// </summary>
    public List<ToolCompositionPairing> Pairings { get; init; } = [];

    /// <summary>
    /// Per-tool capability overrides, authoritative over both the first-party declaration and the
    /// keyword heuristic. The only way to classify a third-party MCP tool the built-in keyword
    /// vocabulary does not recognise, and the only way to correct a keyword false positive.
    /// </summary>
    public List<ToolCapabilityOverride> ToolCapabilities { get; init; } = [];

    /// <summary>
    /// Per-server capability overrides, applied to every tool advertised by that server.
    /// <strong>Additive only</strong> — a server override can add capability bits a tool would not
    /// otherwise have, but can never remove a bit the tool's own declaration, the MCP annotation, or the
    /// keyword heuristic already found. Removing a bit is a loosening decision and must be made per
    /// tool, in writing, with a stated reason — the same rule <see cref="ToolBehaviorExemption.Server"/>
    /// enforces for behaviour gating, for the same reason: a server-wide "this server never sends
    /// outbound" claim is exactly the kind of blanket exemption that quietly re-opens the hole this
    /// feature exists to close.
    /// </summary>
    public List<ToolCapabilityServerOverride> ServerCapabilities { get; init; } = [];
}

/// <summary>The posture for one (source capability, sink capability) pairing.</summary>
public sealed class ToolCompositionPairing
{
    /// <summary>The source-side capability. Must be <see cref="ToolCompositionCapability.IngestsUntrustedInput"/>
    /// or <see cref="ToolCompositionCapability.ReadsCredentials"/> — validated at startup.</summary>
    public ToolCompositionCapability Source { get; init; }

    /// <summary>The sink-side capability. Must be <see cref="ToolCompositionCapability.WritesFiles"/>,
    /// <see cref="ToolCompositionCapability.ExecutesCode"/>, or <see cref="ToolCompositionCapability.SendsOutbound"/> —
    /// validated at startup.</summary>
    public ToolCompositionCapability Sink { get; init; }

    /// <summary>The posture for this pairing.</summary>
    public CompositionPosture Posture { get; init; }
}

/// <summary>One tool's capability override, and why.</summary>
public sealed class ToolCapabilityOverride
{
    /// <summary>The tool name, matched case-insensitively against the tool's published name.</summary>
    public string Tool { get; init; } = string.Empty;

    /// <summary>
    /// The MCP server this override is for. Required when <see cref="Capabilities"/> is empty — i.e.
    /// this override clears bits another source found — because clearing a name-keyed tool's
    /// capabilities without naming its server hands back the exact bypass
    /// <c>ToolBehaviorExemption.Server</c> exists to prevent: a tool name belongs to nobody, and any
    /// other configured server can advertise a genuinely dangerous tool under the same name tomorrow.
    /// </summary>
    public string? Server { get; init; }

    /// <summary>The capability bits this tool actually has, replacing whatever the first-party
    /// declaration or keyword heuristic found. An empty list clears every bit — see
    /// <see cref="Server"/>'s remarks for why that requires naming the server.</summary>
    public List<ToolCompositionCapability> Capabilities { get; init; } = [];

    /// <summary>Why this override is correct. Required — a blank reason fails startup validation, for
    /// the same reason it does on <see cref="ToolBehaviorExemption.Reason"/>: this list is the first
    /// place a reviewer looks when asking why a tool was never flagged.</summary>
    public string Reason { get; init; } = string.Empty;
}

/// <summary>One MCP server's additive capability override, and why.</summary>
public sealed class ToolCapabilityServerOverride
{
    /// <summary>The MCP server name every advertised tool inherits these capabilities from.</summary>
    public string Server { get; init; } = string.Empty;

    /// <summary>
    /// The capability bits added to every tool this server advertises, on top of whatever the
    /// per-tool sources found. Must be non-empty — see <see cref="ToolCompositionGatingConfig.ServerCapabilities"/>'s
    /// remarks for why a server override may only add.
    /// </summary>
    public List<ToolCompositionCapability> Capabilities { get; init; } = [];

    /// <summary>Why every tool on this server carries these capabilities. Required.</summary>
    public string Reason { get; init; } = string.Empty;
}
