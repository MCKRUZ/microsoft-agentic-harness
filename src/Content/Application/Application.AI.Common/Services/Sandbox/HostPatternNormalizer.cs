using System.Net;
using Domain.Common.Helpers;

namespace Application.AI.Common.Services.Sandbox;

/// <summary>
/// Reduces a host value — a requested host or a configured deny/allow pattern — to the bare,
/// comparable, connect-time canonical form <see cref="CapabilityEnforcer"/>'s host scoping matches
/// against. Extracted from <see cref="CapabilityEnforcer"/> itself (#647) so a config-time check —
/// <c>SandboxConfigValidator</c>'s inert-pattern rule, which needs the identical normalization to
/// avoid becoming a second, driftable copy of it — can call the exact same logic the runtime
/// enforcement path uses, rather than re-implementing it.
/// </summary>
public static class HostPatternNormalizer
{
    private const string WildcardPrefix = "*.";

    /// <summary>
    /// Whether <paramref name="value"/> carries the <see cref="WildcardPrefix"/> a
    /// <c>CapabilityEnforcer.HostPatternMatches</c> suffix pattern uses — the single shared check for
    /// every call site, so an ordinal-vs-culture inconsistency between them can't reopen a mismatch of
    /// the kind #635 was filed to close.
    /// </summary>
    public static bool HasWildcardPrefix(string value) => value.StartsWith(WildcardPrefix, StringComparison.Ordinal);

    /// <summary>
    /// Whether an already-<see cref="NormalizeHostForMatch"/>d <paramref name="normalizedPattern"/>
    /// can ever match any requested host — i.e. is not the class of malformed/unnormalizable pattern
    /// <see cref="Domain.Common.Helpers.SecureInputValidatorHelper.ValidateHost"/> rejects, once its
    /// wildcard prefix (if any) is stripped. The single shared "is this pattern inert" predicate for
    /// both <c>CapabilityEnforcer.WarnIfPatternIsInert</c> (the runtime check, over a list already
    /// normalized by its own caller) and <c>SandboxConfigValidator</c> (the startup check, which
    /// normalizes the raw configured value itself before calling this) — correctness-review found
    /// these two had each grown their own copy of the identical wildcard-strip-then-validate logic
    /// despite this type's own doc comment claiming otherwise.
    /// </summary>
    /// <param name="normalizedPattern">
    /// A pattern already reduced by <see cref="NormalizeHostForMatch"/> — passing a raw, un-normalized
    /// value here answers a different question than intended.
    /// </param>
    public static bool IsMatchableWhenNormalized(string normalizedPattern)
    {
        var checkValue = HasWildcardPrefix(normalizedPattern) ? normalizedPattern[2..] : normalizedPattern;
        return SecureInputValidatorHelper.ValidateHost(checkValue);
    }

    /// <summary>
    /// Reduces a host value — whether a requested host or a configured deny/allow entry — to a bare,
    /// comparable host name, canonicalized the same way the actual network client
    /// (<c>Uri</c>/<c>SocketsHttpHandler</c>) would resolve it — <em>not</em> an ad-hoc scan for
    /// <c>"://"</c>, which matches the first occurrence anywhere in the string rather than only a
    /// leading scheme and so can be pointed at an unrelated embedded URL later in the value. A leading
    /// <c>"*."</c> wildcard prefix is preserved verbatim around the normalized remainder so
    /// <c>CapabilityEnforcer.HostPatternMatches</c>'s suffix check keeps working.
    /// </summary>
    /// <remarks>
    /// #635: a bare-shape value (no leading scheme, no <c>'/'</c>) is now routed through the same
    /// <c>Uri</c> host parser via a synthetic <c>http://</c> scheme, then reduced by
    /// <see cref="CanonicalizeParsedHost"/> to the connect-time form — closing four
    /// deny-list-evasion classes measured against the real .NET BCL and, for the fourth, against the
    /// actual compiled <c>CapabilityEnforcer.EnforceAsync</c>: a decimal/hex/short-form IPv4
    /// literal (<c>2130706433</c>, <c>0x7f.0.0.1</c>, <c>127.1</c>) that a raw string compare never
    /// equated with its dotted-quad deny entry; a Unicode label separator or zero-width character
    /// (<c>evil。com</c>, a trailing U+200B) that <c>Uri.Host</c> preserves verbatim but
    /// <c>Uri.IdnHost</c> — what the connecting client actually uses — normalizes to plain ASCII; a
    /// bracketed/zone-qualified IPv6 literal (<c>[::1]</c>, <c>fe80::1%eth0</c>) that previously had
    /// up to four spellings that failed to match each other or a bare deny entry; and an IPv4-mapped
    /// IPv6 literal (<c>::ffff:127.0.0.1</c>), collapsed to its IPv4 form in
    /// <see cref="CanonicalizeParsedHost"/>. A value containing <c>'/'</c> is deliberately excluded
    /// from synthetic-scheme parsing and keeps the legacy <see cref="StripPort"/> fallback: forcing a
    /// path/query-shaped value through a scheme would let its leading segment before the first
    /// <c>'/'</c> be treated as a host even though it has no leading scheme naming one, which is
    /// exactly the confusion
    /// <c>AllowedHost_UrlWithEmbeddedSchemeLaterInString_DoesNotMatchTheEmbeddedHost</c> exists to
    /// rule out. Verified (throwaway console app against the pinned BCL, 26/26 cases, plus code
    /// review's own live probe of the compiled enforcer): every existing test pair still normalizes
    /// identically, every deny-list bypass class named above closes, and no unrelated host
    /// (<c>notevil.com</c>, <c>xn--vil-9ma.com</c>, a different IP) collides with another. NOT
    /// verified exhaustive — this closes the specific classes above, not every conceivable alternate
    /// host encoding; treat a new one, if found, as its own gap rather than assuming this comment's
    /// list is complete. This <c>Uri</c>-based canonicalization matches every tool that exists TODAY
    /// — not because every tool connects via <c>Uri</c>/<c>HttpClient</c> (several run subprocesses —
    /// terraform, npm, kubectl — via <c>ISandboxExecutor</c>, which could resolve a host differently),
    /// but because grepping every <c>ResourceParametersByOperation</c> declaration in this repo found
    /// zero production tools that declare a <see cref="Domain.AI.Sandbox.ResourceParameterKind.Host"/>
    /// parameter — <c>CapabilityEnforcer.EnforceHostScoping</c> never runs with a non-empty
    /// <c>requestedHosts</c> today regardless of what a given tool's own consumer does with the value.
    /// A template consumer adding a host-taking tool that resolves the raw value through something
    /// other than <c>Uri</c> (a raw socket, a DNS lookup, a subprocess) would need this file's
    /// normalization to actually match — worth re-verifying at that point rather than assuming this
    /// comment still holds.
    /// </remarks>
    public static string NormalizeHostForMatch(string value)
    {
        var trimmed = value.Trim();

        return HasWildcardPrefix(trimmed)
            ? "*." + NormalizeBareHost(trimmed[2..])
            : NormalizeBareHost(trimmed);
    }

    /// <summary>
    /// Normalizes a value with any leading <c>"*."</c> wildcard prefix already stripped — either a
    /// full absolute URI or a bare host[:port]/IP literal, never a wildcard pattern itself.
    /// </summary>
    /// <remarks>
    /// CI security-review (post-merge-attempt) found a second-order gap in this normalization: a
    /// non-ASCII digit (a fullwidth <c>２</c>, U+FF12) defeats <see cref="Uri"/>'s own up-front IPv4-
    /// literal recognition — <c>Uri.TryCreate("http://２852039166/")</c> classifies this as
    /// <c>HostNameType.Dns</c>, not <c>IPv4</c>, because the leading character isn't an ASCII digit at
    /// parse time. <see cref="CanonicalizeParsedHost"/>'s <c>IdnHost</c> step then IDNA/NFKC-folds the
    /// fullwidth digit to plain ASCII as a side effect of DNS-label normalization — producing
    /// <c>"2852039166"</c>, a pure-ASCII decimal string that IS a legacy decimal-IPv4 encoding of
    /// <c>169.254.169.254</c> (the cloud metadata address), but <c>Uri</c> never re-evaluates
    /// <c>HostNameType</c> against its own normalized output, so the value is never collapsed to the
    /// dotted-quad form the very first #635 fix already handles for a plain (all-ASCII) decimal
    /// literal. Verified live: feeding <c>NormalizeBareHostOnce</c>'s own output back into itself a
    /// second time DOES resolve it correctly — the second pass sees pure ASCII digits up front and
    /// <c>Uri</c> recognizes them as IPv4 immediately. <see cref="NormalizeBareHost"/> below re-runs
    /// the single-pass normalization to a fixed point (bounded, not unconditional) specifically to
    /// catch this class regardless of how many confusable/normalization layers an adversarial value
    /// stacks — not just the two observed here.
    /// </remarks>
    private static string NormalizeBareHost(string value)
    {
        var current = value;
        for (var i = 0; i < 4; i++)
        {
            var next = NormalizeBareHostOnce(current);
            if (next == current)
                return next;
            current = next;
        }

        return current;
    }

    private static string NormalizeBareHostOnce(string value)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host))
            return CanonicalizeParsedHost(uri);

        // run-gates correctness/security review: not just '/' — a bare value carrying '@' (userinfo),
        // '#' (fragment), '?' (query), or '\' (a browser/some URI parsers treat this as '/') would
        // otherwise be silently reduced to just the leading host segment by the synthetic-scheme
        // parse below, where it was refused outright as malformed before #635. Harmless for a tool
        // that builds a web request from the result (the real HTTP client resolves the identical
        // leading host), but a template consumer's future tool that feeds the raw value to a DNS
        // lookup, a raw socket, or a subprocess would be checked against a different string than it
        // actually contacts — the same "checked value must match consumed value" hazard #635 exists
        // to close, just for a shape no tool in this repo produces today. Kept alongside '/' in the
        // legacy StripPort fallback rather than synthetic-parsed.
        if (value.IndexOfAny(['/', '@', '#', '?', '\\']) >= 0)
            return StripPort(value).TrimEnd('.');

        // Bare IPv6 needs brackets to be syntactically valid inside a URI ("http://::1/" is not a
        // parseable URI); a lone ':' is a port separator (StripPort's job below), not IPv6, so only
        // wrap when there's more than one — the same signal StripPort itself uses to leave a bare
        // IPv6 literal untouched.
        var candidate = !value.StartsWith('[') && value.Count(c => c == ':') > 1 ? $"[{value}]" : value;

        return Uri.TryCreate($"http://{candidate}/", UriKind.Absolute, out var synthetic) && !string.IsNullOrEmpty(synthetic.Host)
            ? CanonicalizeParsedHost(synthetic)
            : StripPort(value).TrimEnd('.');
    }

    /// <summary>
    /// A value <see cref="NormalizeBareHost"/> guarantees fails
    /// <see cref="Domain.Common.Helpers.SecureInputValidatorHelper.ValidateHost"/> (its explicit
    /// embedded-NUL check), returned when <see cref="Uri.IdnHost"/> throws instead of falling back to
    /// the un-normalized <see cref="Uri.Host"/>. Falling back to <c>Host</c> would silently
    /// reintroduce the exact Unicode-label-separator/zero-width-character evasion class #635 exists to
    /// close, on precisely the adversarial input that triggered the exception — code review (#635)
    /// found the original silent fallback did exactly this with no signal it had fired. This sentinel
    /// instead composes with existing, already-logged machinery: on the requested-host side it makes
    /// <c>CapabilityEnforcer.ValidateHosts</c> refuse the call outright (already logged as "host
    /// denied"); on a configured pattern it makes that one entry permanently unmatchable, which
    /// <c>CapabilityEnforcer.WarnIfPatternIsInert</c> (or, since #647, <c>SandboxConfigValidator</c>'s
    /// startup rule) already reports as an inert-configuration problem. Either way the failure is
    /// observable through logging or startup validation this normalization already feeds, rather than
    /// adding a new logging path for an exception verified (round-2 code review) to actually occur — a
    /// mixed valid-character-plus-invalid-Unicode label throws <see cref="UriFormatException"/> from
    /// <see cref="Uri.IdnHost"/>, not merely a hypothetical this sentinel guards against defensively.
    /// </summary>
    private const string UnnormalizableHostSentinel = "\0";

    /// <summary>
    /// Reduces a successfully-parsed <see cref="Uri"/> to the bracket-free, zone-free, IPv4-collapsed,
    /// connect-time canonical host <see cref="Uri.IdnHost"/> resolves to.
    /// </summary>
    private static string CanonicalizeParsedHost(Uri uri)
    {
        string host;
        try
        {
            // Uri.IdnHost, not Uri.Host: Host preserves a Unicode label separator or a bracketed IPv6
            // literal verbatim, which is exactly the class of string a raw compare against a deny
            // entry misses (#635). Round-2 code review found IdnHost DOES throw
            // UriFormatException for a mixed valid-character-plus-invalid-Unicode label (verified:
            // "a￿.com" — a pure-invalid label fails earlier, at Uri.TryCreate itself, and never
            // reaches this property; a mixed one reaches it and throws) — correcting this file's
            // earlier, narrower empirical claim. See UnnormalizableHostSentinel for why the fallback
            // on that throw is NOT uri.Host.
            host = uri.IdnHost;
        }
        catch (Exception)
        {
            return UnnormalizableHostSentinel;
        }

        if (host.StartsWith('[') && host.EndsWith(']'))
            host = host[1..^1];

        // IdnHost retains an IPv6 zone identifier verbatim, but two spellings of the identical zone
        // ("%eth0" bare vs. the percent-escaped "%25eth0") do not string-equal each other — truncate
        // it outright rather than trying to normalize its encoding.
        var zoneIndex = host.IndexOf('%');
        if (zoneIndex >= 0)
            host = host[..zoneIndex];

        // #635 code review: an IPv4-mapped IPv6 literal ("::ffff:127.0.0.1") is well-formed IPv6, and
        // IdnHost does not collapse it to the equivalent IPv4 form — so it never string-equals a plain
        // IPv4 deny/allow entry for the identical address. Verified live against the compiled
        // enforcer: a DeniedHosts=["127.0.0.1"] entry did not refuse a requested "::ffff:127.0.0.1"
        // before this. Mirrors the existing normalization in
        // Infrastructure.AI.Hooks.CompositeHookExecutor.IsReservedAddress for the identical address
        // class, rather than inventing a second way to do the same collapse.
        if (IPAddress.TryParse(host, out var ip))
        {
            if (ip.IsIPv4MappedToIPv6)
                host = ip.MapToIPv4().ToString();
            else if (TryGetDeprecatedIPv4CompatibleForm(ip, out var ipv4))
                host = ipv4.ToString();
        }

        return host.TrimEnd('.');
    }

    /// <summary>
    /// Round-2 code review: the older, RFC 4291-deprecated "IPv4-compatible" IPv6 form
    /// (<c>::a.b.c.d</c>, no <c>ffff</c> prefix — distinct from the IPv4-<em>mapped</em> form
    /// <see cref="IPAddress.IsIPv4MappedToIPv6"/> already handles above) still parses successfully
    /// today and is not covered by that property. Verified: <c>::127.0.0.1</c> and its equivalent
    /// compressed form <c>::7f00:1</c> both parse to the identical address, with
    /// <c>IsIPv4MappedToIPv6</c> false for both — so without this, a plain
    /// <c>DeniedHosts=["127.0.0.1"]</c> entry does not refuse either spelling.
    /// </summary>
    /// <remarks>
    /// Deliberately NOT a blind "first 12 bytes zero → take the last 4" check: <c>::1</c> (loopback)
    /// and <c>::</c> (unspecified) both have an all-zero first-12-byte prefix too, but are reserved
    /// addresses with their own distinct meaning, not IPv4-compatible encodings — verified naively
    /// extracting the last 4 bytes of <c>::1</c> gives <c>0.0.0.1</c>, not <c>127.0.0.1</c>, which
    /// would be an outright WRONG collapse (misidentifying loopback as an unrelated address), not
    /// merely an incomplete one. Excluded via the same well-tested <see cref="IPAddress.IsLoopback"/>
    /// / <see cref="IPAddress.IPv6Any"/> checks the BCL itself uses, rather than a hand-rolled
    /// special-case list.
    /// </remarks>
    private static bool TryGetDeprecatedIPv4CompatibleForm(IPAddress ip, out IPAddress ipv4)
    {
        ipv4 = IPAddress.None;

        if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)
            return false;

        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.IPv6Any))
            return false;

        var bytes = ip.GetAddressBytes();
        for (var i = 0; i < 12; i++)
        {
            if (bytes[i] != 0)
                return false;
        }

        ipv4 = new IPAddress(bytes[12..]);
        return true;
    }

    /// <summary>
    /// Strips a trailing <c>:port</c> from <paramref name="host"/>, recognizing the bracketed IPv6
    /// form (<c>[::1]:443</c>) and leaving a <em>bare</em> IPv6 literal (<c>::1</c>) untouched — more
    /// than one colon with no brackets is never a host:port pair, so treating the last colon as a
    /// port separator there would truncate the address itself (<c>::1</c> otherwise becomes <c>:</c>
    /// and can never match a configured deny/allow entry for it).
    /// </summary>
    private static string StripPort(string host)
    {
        if (host.StartsWith('['))
        {
            var closeBracket = host.IndexOf(']');
            return closeBracket > 0 ? host[1..closeBracket] : host;
        }

        if (host.Count(c => c == ':') != 1)
            return host;

        var colonIndex = host.IndexOf(':');
        return colonIndex > 0 && host[(colonIndex + 1)..].All(char.IsDigit)
            ? host[..colonIndex]
            : host;
    }
}
