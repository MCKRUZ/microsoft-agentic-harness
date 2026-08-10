namespace Domain.Common.Config.AI.Governance;

/// <summary>
/// Requires human approval for every tool that has not declared itself read-only, rather than for
/// every tool somebody remembered to put on a list. Bound from
/// <c>AppConfig:AI:Governance:ToolBehaviorGating</c> in appsettings.json.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What this changes.</strong> Tool governance decides from lists of names — a plugin's
/// allow and deny lists, per-agent authorization, YAML policy rules. Every one of them requires
/// somebody to enumerate the dangerous tools correctly, forever, including tools that arrive at
/// runtime from an MCP server nobody on the team wrote. A tool nobody thought to list is callable by
/// default, and the lists cannot tell <c>create_page</c> from <c>search_pages</c> because names carry
/// no behaviour. With this enabled the default inverts: a tool is gated unless its own declaration
/// says it only reads, so a new mutating tool appearing upstream is gated the moment it appears and
/// nobody edits anything.
/// </para>
/// <para>
/// <strong>Silence is not a claim.</strong> A tool that declares nothing is treated as one that
/// writes. This is the same invariant as an unresolved knowledge scope meaning global rather than
/// private: unknown must never resolve to safe, because every path that fails to establish a fact
/// then lands on the permissive answer.
/// </para>
/// <para>
/// <strong>Off by default, and it needs company.</strong> The posture is enforced by the tool
/// governor, so it does nothing unless <see cref="GovernanceConfig.EnforceToolInvocation"/> is also
/// on; and the resulting verdict only reaches a person when
/// <see cref="ToolApprovalConfig.Enabled"/> and <see cref="EscalationConfig.Enabled"/> are on too —
/// without those the call is refused rather than asked about, which is safe but will read to users as
/// tools mysteriously failing. <c>GovernanceConfigValidator</c> refuses to start a host that switches
/// this on without enforcement, rather than leaving a switched-on control that does nothing.
/// </para>
/// </remarks>
public sealed class ToolBehaviorGatingConfig
{
    /// <summary>
    /// Whether a tool that has not declared itself read-only requires human approval before it runs.
    /// Off by default.
    /// </summary>
    public bool RequireApprovalForNonReadOnlyTools { get; init; }

    /// <summary>
    /// Tools exempted from the posture by name, each with the reason it is exempt.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is a name list, deliberately, and it is the honest part.</strong> A behaviour
    /// declaration is a hint, and hints are sometimes wrong in the direction that costs an operator an
    /// approval prompt on every call of a tool that plainly only reads — a search endpoint that uses
    /// POST and is therefore assumed to write, for instance. Denying that escape hatch does not make
    /// the annotations more accurate; it makes operators switch the whole posture off.
    /// </para>
    /// <para>
    /// The reason is required rather than optional, and <c>GovernanceConfigValidator</c> rejects a
    /// blank one at startup. An exemption whose justification nobody wrote down is indistinguishable a
    /// year later from one added to silence a prompt, and this list is the first place a reviewer looks
    /// when asking why a tool was never gated.
    /// </para>
    /// </remarks>
    public List<ToolBehaviorExemption> Exemptions { get; init; } = [];
}

/// <summary>
/// One tool exempted from the non-read-only approval posture, and why.
/// </summary>
public sealed class ToolBehaviorExemption
{
    /// <summary>The tool name, matched case-insensitively against the name on the call.</summary>
    public string Tool { get; init; } = string.Empty;

    /// <summary>
    /// The MCP server this exemption is for. Required when the tool comes from a server that is not
    /// marked trusted; may be omitted for a first-party tool or one from a trusted server.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Without this, an exemption is a bypass any configured server can claim.</strong> A tool
    /// name is not owned by anyone: an operator exempts <c>notion_search</c> for the vendor server they
    /// checked, and a second, unvouched-for server advertises a destructive tool by the same name. The
    /// behaviour registry defends against exactly that — a shadowing server can only ever tighten a
    /// record it did not create — and a name-only exemption applied on top would hand back the bypass
    /// the registry had just refused.
    /// </para>
    /// <para>
    /// So an exemption is honoured for an unvouched-for server's tool only when it names that server.
    /// Naming it is a much narrower statement than
    /// <c>McpServerDefinition.TrustToolAnnotations</c> — that accepts every declaration a server makes,
    /// present and future, while this accepts one tool the operator has actually looked at.
    /// </para>
    /// </remarks>
    public string? Server { get; init; }

    /// <summary>
    /// Why this tool is exempt despite not declaring itself read-only. Required — a blank reason fails
    /// startup validation.
    /// </summary>
    public string Reason { get; init; } = string.Empty;
}
