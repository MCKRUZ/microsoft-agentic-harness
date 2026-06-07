using System.Net;
using System.Net.Sockets;
using Domain.AI.Egress;
using Domain.AI.Identity;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Egress;

/// <summary>
/// Default <see cref="IEgressPolicy"/> implementation. Reads a single immutable
/// <see cref="EgressAllowlistEntry"/> list at construction and matches each
/// outbound URI against the entries by host (exact OR leftmost-label wildcard),
/// scheme, and port.
/// </summary>
/// <remarks>
/// <para>
/// Default-deny: a request that does not match any entry produces a deny
/// decision. The entry list is frozen at construction; the policy is
/// thread-safe and lock-free on the read path. Consumers who need per-skill
/// allowlists wire a custom <see cref="IEgressPolicy"/> via
/// <c>IEgressPolicyResolver</c>; this implementation is the configuration-bound
/// fallback that PR-3b ships.
/// </para>
/// <para>
/// Wildcards are limited to a leading <c>*.</c> followed by a literal suffix
/// containing at least one dot — <c>*.azure-api.net</c> matches
/// <c>foo.azure-api.net</c> and <c>foo.bar.azure-api.net</c> but NOT the bare
/// suffix <c>azure-api.net</c>. A more permissive regex on the host portion is
/// itself an SSRF vector and is rejected at construction time by the resolver.
/// </para>
/// <para>
/// DNS resolution is performed at decision time only when an entry matches
/// (best-effort) so the decision audit can record the resolved IP. The
/// connect-time IP check that defeats DNS rebinding is performed by the inner
/// SSRF handler, not this policy. Resolution failure does NOT change the
/// verdict — the inner handler will fail the connection on its own resolution
/// pass if the host is unreachable.
/// </para>
/// </remarks>
public sealed class DefaultEgressPolicy : IEgressPolicy
{
    private readonly IReadOnlyList<EgressAllowlistEntry> _entries;
    private readonly ILogger<DefaultEgressPolicy> _logger;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes a new <see cref="DefaultEgressPolicy"/> with the supplied allowlist.</summary>
    /// <param name="entries">Frozen allowlist. May be empty (default-deny).</param>
    /// <param name="logger">Logger for diagnostics.</param>
    /// <param name="timeProvider">Time provider used for <see cref="EgressDecision.DecidedAt"/>.</param>
    public DefaultEgressPolicy(
        IReadOnlyList<EgressAllowlistEntry> entries,
        ILogger<DefaultEgressPolicy> logger,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(timeProvider);

        ValidateEntries(entries);

        _entries = entries;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public async Task<EgressDecision> AllowAsync(
        Uri target,
        AgentIdentity identity,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(identity);

        var now = _timeProvider.GetUtcNow();

        if (!target.IsAbsoluteUri)
        {
            return new EgressDecision
            {
                Allowed = false,
                Reason = "Target URI is not absolute.",
                Target = target,
                DecidedAt = now
            };
        }

        // Enforce http/https before any host matching — non-HTTP schemes are
        // not just "no match found", they are categorically rejected.
        if (!IsHttpScheme(target.Scheme))
        {
            return new EgressDecision
            {
                Allowed = false,
                Reason = $"Scheme '{target.Scheme}' is not permitted (only http and https are allowed).",
                Target = target,
                DecidedAt = now
            };
        }

        foreach (var entry in _entries)
        {
            if (!HostMatches(entry, target.Host))
            {
                continue;
            }

            if (!SchemeMatches(entry, target.Scheme))
            {
                continue;
            }

            if (!PortMatches(entry, target.Port))
            {
                continue;
            }

            var matched = entry.Host ?? entry.HostPattern ?? "(unknown)";
            var resolvedIp = await TryResolveAsync(target.Host, cancellationToken).ConfigureAwait(false);

            return new EgressDecision
            {
                Allowed = true,
                Reason = "Matched allowlist entry.",
                MatchedAllowlistEntry = matched,
                FinalIpAddress = resolvedIp,
                Target = target,
                DecidedAt = now
            };
        }

        return new EgressDecision
        {
            Allowed = false,
            Reason = "No allowlist entry matched (host, scheme, port).",
            Target = target,
            DecidedAt = now
        };
    }

    private static bool IsHttpScheme(string scheme) =>
        string.Equals(scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
        || string.Equals(scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);

    private static bool HostMatches(EgressAllowlistEntry entry, string requestHost)
    {
        if (!string.IsNullOrEmpty(entry.Host))
        {
            return string.Equals(entry.Host, requestHost, StringComparison.OrdinalIgnoreCase);
        }

        if (!string.IsNullOrEmpty(entry.HostPattern))
        {
            return LeftmostWildcardMatches(entry.HostPattern, requestHost);
        }

        return false;
    }

    /// <summary>
    /// Matches a <c>*.example.com</c> pattern against a host. The wildcard
    /// stands in for EXACTLY ONE leading DNS label — TLS certificate semantics
    /// (RFC 6125). <c>foo.example.com</c> matches; <c>foo.bar.example.com</c>
    /// does NOT match (would require <c>*.bar.example.com</c>); the bare suffix
    /// <c>example.com</c> does NOT match.
    /// </summary>
    private static bool LeftmostWildcardMatches(string pattern, string host)
    {
        // Pattern is validated as "*.suffix" with at least one dot in the suffix.
        var suffix = pattern[2..]; // strip leading "*."

        // Reject the bare suffix — there must be exactly one leading label.
        if (string.Equals(host, suffix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Host must end in ".suffix" with at least one char before the dot.
        if (host.Length <= suffix.Length + 1)
        {
            return false;
        }

        var dotBefore = host[host.Length - suffix.Length - 1];
        if (dotBefore != '.')
        {
            return false;
        }

        var hostTail = host[^suffix.Length..];
        if (!string.Equals(hostTail, suffix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // The leading portion must be exactly one DNS label — no dots allowed.
        var leadingLabel = host[..(host.Length - suffix.Length - 1)];
        return !leadingLabel.Contains('.', StringComparison.Ordinal) && leadingLabel.Length > 0;
    }

    private static bool SchemeMatches(EgressAllowlistEntry entry, string scheme)
    {
        if (entry.Schemes.Count == 0)
        {
            return false;
        }

        foreach (var s in entry.Schemes)
        {
            if (string.Equals(s, scheme, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool PortMatches(EgressAllowlistEntry entry, int port)
    {
        if (entry.Ports.Count == 0)
        {
            return false;
        }

        foreach (var p in entry.Ports)
        {
            if (p == port)
            {
                return true;
            }
        }

        return false;
    }

    private async Task<string?> TryResolveAsync(string host, CancellationToken cancellationToken)
    {
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
            if (addresses.Length == 0)
            {
                return null;
            }

            // Prefer IPv4 for audit readability, fall back to the first address otherwise.
            foreach (var addr in addresses)
            {
                if (addr.AddressFamily == AddressFamily.InterNetwork)
                {
                    return addr.ToString();
                }
            }
            return addresses[0].ToString();
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException or OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to resolve host '{Host}' for audit annotation.", host);
            return null;
        }
    }

    private static void ValidateEntries(IReadOnlyList<EgressAllowlistEntry> entries)
    {
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            ArgumentNullException.ThrowIfNull(entry);

            var hasHost = !string.IsNullOrWhiteSpace(entry.Host);
            var hasPattern = !string.IsNullOrWhiteSpace(entry.HostPattern);

            if (hasHost == hasPattern)
            {
                throw new ArgumentException(
                    $"Allowlist entry [{i}] must set exactly one of Host or HostPattern.",
                    nameof(entries));
            }

            if (hasPattern)
            {
                ValidateHostPattern(entry.HostPattern!, i);
            }
        }
    }

    private static void ValidateHostPattern(string pattern, int index)
    {
        // Only allowed pattern: "*." followed by a literal suffix with at least
        // one '.' and no further wildcards or regex metacharacters.
        if (!pattern.StartsWith("*.", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Allowlist entry [{index}] HostPattern '{pattern}' must start with '*.' (leftmost-label wildcard only).",
                nameof(pattern));
        }

        var suffix = pattern[2..];

        if (suffix.Length == 0 || !suffix.Contains('.', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Allowlist entry [{index}] HostPattern '{pattern}' suffix must contain at least one dot.",
                nameof(pattern));
        }

        foreach (var ch in suffix)
        {
            // Reject anything that isn't a normal DNS label character or a dot.
            // Bans '*', '?', '[', ']', and other regex/glob characters.
            var ok = char.IsLetterOrDigit(ch) || ch == '-' || ch == '.';
            if (!ok)
            {
                throw new ArgumentException(
                    $"Allowlist entry [{index}] HostPattern '{pattern}' contains invalid character '{ch}'. Only DNS-safe characters allowed.",
                    nameof(pattern));
            }
        }
    }
}
