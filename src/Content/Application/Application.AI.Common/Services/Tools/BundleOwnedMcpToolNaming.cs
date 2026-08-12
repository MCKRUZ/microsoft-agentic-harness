namespace Application.AI.Common.Services.Tools;

/// <summary>
/// Computes the collision-proof, model-facing name for a tool resolved via a bundle's own (bundle-
/// authored, untrusted) MCP server.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this exists.</strong> Making a bundle's own MCP server usable requires granting the
/// tool names it publishes into the run's <c>CapabilityEnvelope.AllowedTools</c> — but the invocation-
/// time governance gate (<c>ToolInvocationGovernor.EnvelopeGrantsToolWhenArmed</c>) authorizes purely
/// by NAME, with no notion of which server a call actually resolves through. Granting a bundle's
/// self-reported tool name verbatim would let a malicious bundle advertise a tool literally named
/// after a real, more privileged host tool (reachable via keyed DI or a host-configured MCP server)
/// and get that unrelated tool auto-granted by name coincidence — a privilege escalation that costs
/// the attacker nothing but picking a string.
/// </para>
/// <para>
/// Namespacing the tool's own callable name to its originating server — not just internal bookkeeping,
/// but the actual name published to the model and checked at invocation — closes this by construction:
/// the granted name can only ever have come from this one server, for this one bundle, because the
/// namespace prefix is derived from the host-generated, attacker-uncontrolled bundle id, never from
/// anything the bundle author chooses.
/// </para>
/// <para>
/// Both the publisher (<c>ToolChainBuilder</c>, which wraps the tool the model calls) and the granter
/// (<c>BundleRunExecutor</c>, which populates <c>AllowedTools</c>) call this SAME function, so the
/// published name and the granted name can never drift apart.
/// </para>
/// </remarks>
public static class BundleOwnedMcpToolNaming
{
    private const string Separator = "__";

    /// <summary>
    /// Builds the namespaced tool name for <paramref name="rawToolName"/> as published by
    /// <paramref name="namespacedServerName"/> (the bundle-scoped <c>{bundleId}:{serverName}</c> key
    /// under which the server itself is registered). The server portion is sanitized to the character
    /// set most LLM providers require for a function name; the tool's own name is left exactly as the
    /// server declared it, matching how any other MCP tool name is trusted today.
    /// </summary>
    public static string BuildToolName(string namespacedServerName, string rawToolName)
        => $"{Sanitize(namespacedServerName)}{Separator}{rawToolName}";

    /// <summary>
    /// Whether <paramref name="serverName"/> is a bundle-owned, namespaced server key
    /// (<c>{BundleId}:{serverName}</c>, per <see cref="Domain.AI.Bundles.StagedBundle.McpServerNames"/>)
    /// rather than a plain, host-configured server name. Host-configured names never contain a colon —
    /// <c>BundleStagingService.RegisterBundleMcpServers</c> is the only producer of a colon-bearing
    /// server key in this codebase — so this is a reliable, non-heuristic test everywhere a caller
    /// holds an already-resolved server name (as opposed to <c>ToolChainBuilder.ResolveEffectiveMcpServerName</c>,
    /// which instead suffix-matches a skill's bare declared name against the granted list).
    /// </summary>
    public static bool IsNamespacedServerName(string serverName) => serverName.Contains(':');

    private static string Sanitize(string value)
    {
        var chars = new char[value.Length];
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            chars[i] = char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '_';
        }
        return new string(chars);
    }
}
