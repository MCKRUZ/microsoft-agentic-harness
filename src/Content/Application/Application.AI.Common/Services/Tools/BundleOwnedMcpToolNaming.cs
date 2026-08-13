using System.Security.Cryptography;
using System.Text;

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
    /// The hard cap OpenAI and Azure OpenAI both enforce on a function/tool name
    /// (<c>^[a-zA-Z0-9_-]{1,64}$</c>). A name that exceeds this is rejected outright, failing every turn
    /// of the run — see <see cref="Shorten"/>.
    /// </summary>
    private const int MaxToolNameLength = 64;

    /// <summary>Bytes of the content hash kept in a disambiguating suffix, rendered as hex (twice this many characters).</summary>
    private const int HashSuffixByteCount = 5;

    /// <summary>
    /// Builds the namespaced tool name for <paramref name="rawToolName"/> as published by
    /// <paramref name="namespacedServerName"/> (the bundle-scoped <c>{bundleId}:{serverName}</c> key
    /// under which the server itself is registered). The server portion is sanitized to the character set
    /// OpenAI and Azure OpenAI both require for a function name (<c>^[a-zA-Z0-9_-]{1,64}$</c>); the tool's
    /// own (untrusted, bundle-server-declared) name goes through the same sanitization via
    /// <see cref="SanitizeToolName"/> — a bundle-authored MCP server can name its tool anything, including
    /// spaces or punctuation no provider's function-calling API accepts, and an unsanitized name fails
    /// every turn of the run exactly like an over-length one does. When the sanitized concatenation would
    /// still exceed <see cref="MaxToolNameLength"/> — routine, not just adversarial, for an ordinary bundle
    /// id + server name + tool name — the result is deterministically shortened by <see cref="Shorten"/>
    /// instead, so the published name always stays within the limit every provider enforces.
    /// </summary>
    public static string BuildToolName(string namespacedServerName, string rawToolName)
    {
        var full = $"{Sanitize(namespacedServerName)}{Separator}{SanitizeToolName(rawToolName)}";
        return full.Length <= MaxToolNameLength ? full : Shorten(full);
    }

    /// <summary>
    /// Sanitizes <paramref name="rawToolName"/> to the provider charset. When the raw name was already
    /// clean, <see cref="Sanitize"/> is a no-op and the result is returned as-is — preserving a short,
    /// readable name for the overwhelmingly common case. When sanitization actually changes the name, it
    /// necessarily collapses information (multiple distinct raw characters all map to <c>'_'</c>), so two
    /// different raw tool names from the same server can sanitize to the identical string (e.g. "get user"
    /// and "get.user" both become "get_user") — silently merging two different tools into one published
    /// name, which the tool-chain's dedup-by-name step would then drop one of without any signal. A content
    /// hash of the ORIGINAL raw name is appended whenever sanitization was non-trivial, so distinct raw
    /// names stay distinct after sanitizing; two raw names that are already letter-for-letter identical
    /// correctly still produce the same result, since they name the same tool.
    /// </summary>
    private static string SanitizeToolName(string rawToolName)
    {
        var sanitized = Sanitize(rawToolName);
        return sanitized == rawToolName ? sanitized : $"{sanitized}_{HexHashPrefix(rawToolName)}";
    }

    /// <summary>
    /// Fits an over-length namespaced name into <see cref="MaxToolNameLength"/> characters by keeping a
    /// leading slice of it (long enough to always retain the host-generated bundle-id prefix, since that
    /// prefix — never the server or tool name — is what the security property in
    /// <see cref="BundleOwnedMcpToolNaming"/>'s class remarks relies on being attacker-uncontrolled) and
    /// replacing the rest with a content hash of the FULL untruncated name. The hash is computed over
    /// everything, so two different (server, tool) pairs that share the same truncated head — including
    /// two pairs whose head is empty because the server prefix alone already overflows the budget — still
    /// diverge in the appended hash almost certainly, keeping the scheme collision-resistant without
    /// depending on how much of the head survives. Only called once <paramref name="full"/> is already
    /// confirmed to exceed <see cref="MaxToolNameLength"/>, so the head slice never runs past the end of
    /// <paramref name="full"/>.
    /// </summary>
    private static string Shorten(string full)
    {
        var suffix = $"{Separator}{HexHashPrefix(full)}";
        var head = full[..(MaxToolNameLength - suffix.Length)];
        return $"{head}{suffix}";
    }

    /// <summary>The first <see cref="HashSuffixByteCount"/> bytes of <paramref name="value"/>'s SHA-256 digest, as lowercase hex.</summary>
    private static string HexHashPrefix(string value)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexStringLower(digest.AsSpan(0, HashSuffixByteCount));
    }

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
