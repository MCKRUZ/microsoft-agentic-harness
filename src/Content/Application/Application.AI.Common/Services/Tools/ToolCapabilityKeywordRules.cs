using System.Text.RegularExpressions;
using Domain.AI.Governance;
using Domain.Common.Config.AI.Governance;

namespace Application.AI.Common.Services.Tools;

/// <summary>
/// The narrow, built-in keyword vocabulary that classifies a third-party tool's capabilities from its
/// published name, when no first-party declaration, MCP annotation, or operator override answers first.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Token-based, never substring.</strong> The published name is split on <c>_ - .</c> and camel
/// boundaries — including the <c>__</c> bundle-namespace separator, which splits into an empty token and
/// is filtered out, so <c>bundleid_srv__web_fetch</c> still yields the <c>fetch</c> token from the
/// original tool name. Each rule matches whole tokens or an adjacent-token pair, never a fragment inside
/// a longer word — matching a fragment is exactly how a rule silently widens over time.
/// </para>
/// <para>
/// <strong>The exclusion list is the design, not an oversight.</strong> <c>read, get, list, search,
/// query, load, open, write, create, update, sync, call, request, submit, run, key, auth, token</c> are
/// deliberately absent from every rule below. Each tags a large fraction of any real tool set:
/// <c>read</c> alone would mark every file, database, and memory lookup as an untrusted source;
/// bare <c>run</c> would catch <c>run_skill_script</c>. This is the AgentHound design note this feature
/// is built on, carried over verbatim: "keyword sets are kept deliberately narrow — universal taint
/// destroys signal." A keyword added here without an equally deliberate argument for why it will not
/// flag ordinary tools is a regression, not an improvement.
/// </para>
/// <para>
/// <strong>What this buys, and what it does not.</strong> This heuristic is expected to classify a
/// minority of any real third-party MCP tool estate — the design accepts that trade explicitly, because
/// the alternative (a wide vocabulary) fails the acceptance criteria's control case: an agent holding
/// only a source tool, or only a sink tool, must never produce a finding. A narrow vocabulary that
/// classifies less is preferable to a wide one that classifies wrong.
/// </para>
/// </remarks>
public static class ToolCapabilityKeywordRules
{
    // Splits on the standard tool-name separators plus a camelCase boundary. `__` (the bundle-namespace
    // separator) produces an empty entry between the two underscores, filtered out below rather than
    // guarded against here — RegexOptions.Compiled because this runs once per tool per resolution.
    private static readonly Regex Tokenizer = new(
        @"[_\-.]+|(?<=[a-z0-9])(?=[A-Z])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly string[] IngestsUntrustedInputTokens =
        ["fetch", "browse", "crawl", "scrape"];

    private static readonly string[] SendsOutboundTokens =
        ["send", "email", "webhook", "publish", "upload", "notify"];

    private static readonly string[] ExecutesCodeTokens =
        ["exec", "execute", "shell", "bash", "terminal", "eval", "spawn"];

    private static readonly string[] ReadsCredentialsTokens =
        ["secret", "credential", "keyvault", "vault", "password"];

    /// <summary>
    /// Classifies <paramref name="publishedToolName"/> by matching whole tokens (and a small number of
    /// adjacent-token pairs) against the narrow keyword vocabulary. Returns
    /// <see cref="ToolCompositionCapability.None"/> when nothing matches — the common case, and the deliberate one.
    /// </summary>
    public static ToolCompositionCapability Classify(string publishedToolName)
    {
        if (string.IsNullOrWhiteSpace(publishedToolName))
            return ToolCompositionCapability.None;

        var tokens = Tokenizer.Split(publishedToolName)
            .Where(t => t.Length > 0)
            .Select(t => t.ToLowerInvariant())
            .ToArray();

        if (tokens.Length == 0)
            return ToolCompositionCapability.None;

        var tokenSet = new HashSet<string>(tokens, StringComparer.Ordinal);
        var capability = ToolCompositionCapability.None;

        if (ContainsAny(tokenSet, IngestsUntrustedInputTokens)
            || HasAdjacentPair(tokens, IsWebOrHttp, t => t is "search")
            || HasAdjacentPair(tokens, t => t is "read", IsEmailLike))
        {
            capability |= ToolCompositionCapability.IngestsUntrustedInput;
        }

        if (ContainsAny(tokenSet, SendsOutboundTokens)
            || HasAdjacentPair(tokens, t => t is "http", t => t is "post"))
        {
            capability |= ToolCompositionCapability.SendsOutbound;
        }

        if (ContainsAny(tokenSet, ExecutesCodeTokens)
            || HasAdjacentPair(tokens, t => t is "run", IsCommandLike))
        {
            capability |= ToolCompositionCapability.ExecutesCode;
        }

        if (HasAdjacentPair(tokens, IsWriteLike, IsFileLike))
            capability |= ToolCompositionCapability.WritesFiles;

        if (ContainsAny(tokenSet, ReadsCredentialsTokens))
            capability |= ToolCompositionCapability.ReadsCredentials;

        return capability;
    }

    private static bool ContainsAny(HashSet<string> tokens, string[] candidates)
    {
        foreach (var candidate in candidates)
            if (tokens.Contains(candidate))
                return true;
        return false;
    }

    /// <summary>
    /// Whether two adjacent tokens in <paramref name="tokens"/> satisfy <paramref name="first"/>
    /// followed immediately by <paramref name="second"/>, in either order. Order-insensitive because a
    /// tool name reasonably reads either <c>web_search</c> or <c>search_web</c> for the same capability,
    /// and the token pair — not the word order — is the signal.
    /// </summary>
    private static bool HasAdjacentPair(
        IReadOnlyList<string> tokens, Func<string, bool> first, Func<string, bool> second)
    {
        for (var i = 0; i < tokens.Count - 1; i++)
        {
            if ((first(tokens[i]) && second(tokens[i + 1])) || (second(tokens[i]) && first(tokens[i + 1])))
                return true;
        }
        return false;
    }

    private static bool IsWebOrHttp(string token) => token is "web" or "http";
    private static bool IsEmailLike(string token) => token is "email" or "inbox" or "mail";
    private static bool IsCommandLike(string token) => token is "command" or "script" or "code";
    private static bool IsWriteLike(string token) => token is "write" or "edit" or "save" or "delete";
    private static bool IsFileLike(string token) => token is "file" or "directory" or "path" or "fs";
}
