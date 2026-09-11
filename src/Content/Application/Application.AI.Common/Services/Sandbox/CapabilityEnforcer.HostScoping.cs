using System.Net;
using Domain.AI.Sandbox;
using Domain.Common;
using Domain.Common.Helpers;
using Microsoft.Extensions.Logging;

namespace Application.AI.Common.Services.Sandbox;

/// <summary>
/// Network-host scoping half of <see cref="CapabilityEnforcer"/> (#418) — see that file's remarks for
/// why this is a separate partial rather than folded into the main file.
/// </summary>
public sealed partial class CapabilityEnforcer
{
    /// <summary>
    /// Checks <paramref name="requestedHosts"/> against the profile's host scoping, when any is
    /// configured. Returns <see langword="null"/> on a pass, or the refusal to return. Mirrors
    /// <see cref="EnforcePathScoping"/>.
    /// </summary>
    private Result? EnforceHostScoping(string toolName, IReadOnlyList<string>? requestedHosts, ToolPermissionProfile profile)
    {
        var hasHostScoping = profile.AllowedHosts.Count > 0 || profile.DeniedHosts.Count > 0;
        if (!hasHostScoping)
            return null;

        if (requestedHosts is null)
        {
            _logger.LogWarning(
                "Tool {ToolName} has host scoping configured but no requested host could be determined for this call",
                toolName);
            return Result.Forbidden(
                $"Tool '{toolName}' has host scoping configured but no requested host could be determined for this call.");
        }

        if (requestedHosts.Count == 0)
            return null;

        // Each configured pattern is normalized once per call here, not once per (requested host ×
        // pattern) pair inside the loop below. A null entry (a literal JSON `null` in DeniedHosts/
        // AllowedHosts, binding into a null List<string> element despite the non-nullable element
        // type) is skipped rather than passed to NormalizeHostForMatch, which would NRE on it.
        var deniedPatterns = profile.DeniedHosts.Where(h => h is not null).Select(NormalizeHostForMatch).ToList();
        var allowedPatterns = profile.AllowedHosts.Where(h => h is not null).Select(NormalizeHostForMatch).ToList();

        // #635 LOW: a typo'd or malformed deny/allow entry normalizes to a string ValidateHost
        // would reject on the requested-host side (SecureInputValidatorHelper.ValidateHost is never
        // run on the CONFIGURED side, only the requested side, in ValidateHosts below) — silently
        // becoming a permanent no-op with no signal to the operator that their configuration is
        // inert. Advisory only: log and keep evaluating, since a malformed pattern is harmless (it
        // just never matches anything) rather than a reason to fail every call closed.
        WarnIfPatternIsInert(toolName, "DeniedHosts", deniedPatterns);
        WarnIfPatternIsInert(toolName, "AllowedHosts", allowedPatterns);

        if (ValidateHosts(requestedHosts, deniedPatterns, allowedPatterns) is { } hostViolation)
        {
            _logger.LogWarning("Tool {ToolName} host denied: {Host}", toolName, hostViolation);
            return Result.Forbidden($"Tool '{toolName}' host denied: {hostViolation}");
        }

        return null;
    }

    /// <summary>
    /// Returns the first requested host that violates the pre-normalized <paramref name="deniedPatterns"/>/
    /// <paramref name="allowedPatterns"/>, or <see langword="null"/> if every host is permitted.
    /// Deny-overrides-allow, mirroring <see cref="ValidatePaths"/> (#418, restored from pre-#405 history).
    /// </summary>
    private static string? ValidateHosts(
        IReadOnlyList<string> requestedHosts, IReadOnlyList<string> deniedPatterns, IReadOnlyList<string> allowedPatterns)
    {
        foreach (var host in requestedHosts)
        {
            var normalizedHost = NormalizeHostForMatch(host);

            // Rejects the whole class of malformed input outright — empty, oversized, embedded
            // NUL/control characters, or anything that isn't syntactically a valid host — mirroring
            // ValidatePaths' unconditional NormalizeRequestedPath rejection for paths (#605; #595
            // closed only the narrower empty-string case of this gap). Validated against the
            // NORMALIZED value, not the raw requested value: a legitimate requested host can arrive
            // as a full absolute URI, which NormalizeHostForMatch's own job is to reduce to a bare
            // host — SecureInputValidatorHelper.ValidateHost expects that bare-host shape, so running
            // it against the raw value would reject every URI-shaped legitimate host.
            if (!SecureInputValidatorHelper.ValidateHost(normalizedHost))
                return host;

            if (deniedPatterns.Any(pattern => HostPatternMatches(normalizedHost, pattern)))
                return host;

            if (allowedPatterns.Count > 0 && !allowedPatterns.Any(pattern => HostPatternMatches(normalizedHost, pattern)))
                return host;
        }

        return null;
    }

    /// <summary>
    /// Matches an already-<see cref="NormalizeHostForMatch"/>d host against an already-normalized
    /// pattern, which may be an exact host name or a <c>*.suffix</c> wildcard.
    /// </summary>
    private static bool HostPatternMatches(string normalizedHost, string normalizedPattern)
    {
        if (HasWildcardPrefix(normalizedPattern))
        {
            var suffix = normalizedPattern[1..];
            return normalizedHost.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                   || normalizedHost.Equals(normalizedPattern[2..], StringComparison.OrdinalIgnoreCase);
        }

        return normalizedHost.Equals(normalizedPattern, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reduces a host value — whether a requested host or a configured deny/allow entry — to a bare,
    /// comparable host name, canonicalized the same way the actual network client
    /// (<c>Uri</c>/<c>SocketsHttpHandler</c>) would resolve it — <em>not</em> an ad-hoc scan for
    /// <c>"://"</c>, which matches the first occurrence anywhere in the string rather than only a
    /// leading scheme and so can be pointed at an unrelated embedded URL later in the value. A leading
    /// <c>"*."</c> wildcard prefix is preserved verbatim around the normalized remainder so
    /// <see cref="HostPatternMatches"/>'s suffix check keeps working.
    /// </summary>
    /// <remarks>
    /// #635: a bare-shape value (no leading scheme, no <c>'/'</c>) is now routed through the same
    /// <c>Uri</c> host parser via a synthetic <c>http://</c> scheme, then reduced by
    /// <see cref="CanonicalizeParsedHost"/> to the connect-time form — closing four
    /// deny-list-evasion classes measured against the real .NET BCL and, for the fourth, against the
    /// actual compiled <see cref="CapabilityEnforcer.EnforceAsync"/>: a decimal/hex/short-form IPv4
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
    /// parameter — <see cref="EnforceHostScoping"/> never runs with a non-empty <c>requestedHosts</c>
    /// today regardless of what a given tool's own consumer does with the value. A template consumer
    /// adding a host-taking tool that resolves the raw value through something other than <c>Uri</c>
    /// (a raw socket, a DNS lookup, a subprocess) would need this file's normalization to actually
    /// match — worth re-verifying at that point rather than assuming this comment still holds.
    /// </remarks>
    private static string NormalizeHostForMatch(string value)
    {
        var trimmed = value.Trim();

        return HasWildcardPrefix(trimmed)
            ? "*." + NormalizeBareHost(trimmed[2..])
            : NormalizeBareHost(trimmed);
    }

    private const string WildcardPrefix = "*.";

    /// <summary>
    /// Whether <paramref name="value"/> carries the <see cref="WildcardPrefix"/> a
    /// <see cref="HostPatternMatches"/> suffix pattern uses — the single shared check for all three
    /// call sites, so an ordinal-vs-culture inconsistency between them can't reopen a mismatch of the
    /// kind #635 was filed to close.
    /// </summary>
    private static bool HasWildcardPrefix(string value) => value.StartsWith(WildcardPrefix, StringComparison.Ordinal);

    /// <summary>
    /// Normalizes a value with any leading <c>"*."</c> wildcard prefix already stripped — either a
    /// full absolute URI or a bare host[:port]/IP literal, never a wildcard pattern itself.
    /// </summary>
    private static string NormalizeBareHost(string value)
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
    /// <see cref="SecureInputValidatorHelper.ValidateHost"/> (its explicit embedded-NUL check),
    /// returned when <see cref="Uri.IdnHost"/> throws instead of falling back to the un-normalized
    /// <see cref="Uri.Host"/>. Falling back to <c>Host</c> would silently reintroduce the exact
    /// Unicode-label-separator/zero-width-character evasion class #635 exists to close, on precisely
    /// the adversarial input that triggered the exception — code review (#635) found the original
    /// silent fallback did exactly this with no signal it had fired. This sentinel instead composes
    /// with existing, already-logged machinery: on the requested-host side it makes
    /// <see cref="ValidateHosts"/> refuse the call outright (already logged as "host denied"); on a
    /// configured pattern it makes that one entry permanently unmatchable, which
    /// <see cref="WarnIfPatternIsInert"/> already logs as an inert-configuration warning. Either way
    /// the failure is observable through the logging this file already has, rather than adding a new
    /// logging path for an exception verified (round-2 code review) to actually occur — a mixed
    /// valid-character-plus-invalid-Unicode label throws <see cref="UriFormatException"/> from
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
    /// Logs one warning per already-normalized <paramref name="patterns"/> entry that
    /// <see cref="SecureInputValidatorHelper.ValidateHost"/> would reject — #635 LOW: such an entry
    /// can never match any requested host (every requested host is validated the same way in
    /// <see cref="ValidateHosts"/>), so it is a silent, permanent no-op in the operator's
    /// configuration unless something says so.
    /// </summary>
    private void WarnIfPatternIsInert(string toolName, string configKey, IReadOnlyList<string> patterns)
    {
        foreach (var pattern in patterns)
        {
            var checkValue = HasWildcardPrefix(pattern) ? pattern[2..] : pattern;
            if (!SecureInputValidatorHelper.ValidateHost(checkValue))
            {
                _logger.LogWarning(
                    "Tool {ToolName} has a {ConfigKey} entry that normalizes to an invalid host and can never match any requested host: {Pattern}",
                    toolName, configKey, pattern);
            }
        }
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
