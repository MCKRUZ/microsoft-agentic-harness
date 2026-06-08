using Domain.AI.Egress;
using Domain.AI.Skills;
using FluentValidation;

namespace Application.AI.Common.Skills;

/// <summary>
/// Validates an <see cref="EgressManifest"/> parsed from SKILL.md frontmatter.
/// Enforces the same SSRF-narrow rules the runtime policy applies — multi-label
/// wildcards, regex metacharacters, non-HTTP schemes, and out-of-range ports
/// are rejected at parse time so a malformed manifest never reaches the policy
/// resolver.
/// </summary>
/// <remarks>
/// <para>
/// Validation is mirror-by-design to <c>DefaultEgressPolicy.ValidateHostPattern</c>:
/// a permissive regex on the host portion of an allowlist is itself an SSRF
/// vector (a stray <c>.*</c> reduces the allowlist to a no-op). Rejecting at
/// the manifest entry point gives consumers a clear authoring-time error rather
/// than a silent runtime widening.
/// </para>
/// <para>
/// Scheme validation rejects anything outside http and https — non-HTTP schemes
/// (file://, gopher://, ftp://, ...) are SSRF vectors regardless of host. Port
/// validation rejects 0 and any value above 65535. Each entry must declare
/// exactly one of <c>Host</c> or <c>HostPattern</c>; both-set or neither-set
/// entries are ambiguous and rejected.
/// </para>
/// </remarks>
public sealed class EgressManifestValidator : AbstractValidator<EgressManifest>
{
    /// <summary>Initializes a new <see cref="EgressManifestValidator"/>.</summary>
    public EgressManifestValidator()
    {
        RuleForEach(m => m.Allowlist)
            .SetValidator(new EgressAllowlistEntryValidator());
    }
}

/// <summary>
/// Validates a single <see cref="EgressAllowlistEntry"/> declared inside an
/// <see cref="EgressManifest"/>. Public so consumers who parse allowlists from
/// other sources can re-use the rule set.
/// </summary>
public sealed class EgressAllowlistEntryValidator : AbstractValidator<EgressAllowlistEntry>
{
    private const int MinPort = 1;
    private const int MaxPort = 65535;

    /// <summary>Initializes a new <see cref="EgressAllowlistEntryValidator"/>.</summary>
    public EgressAllowlistEntryValidator()
    {
        RuleFor(e => e)
            .Must(HaveExactlyOneOfHostOrPattern)
            .WithMessage("Allowlist entry must set exactly one of 'host' or 'hostPattern' — both-set or neither-set is ambiguous.");

        When(e => !string.IsNullOrWhiteSpace(e.HostPattern), () =>
        {
            RuleFor(e => e.HostPattern!)
                .Must(BeLeftmostLabelWildcard)
                .WithMessage("'hostPattern' must be a leftmost-label wildcard of the form '*.suffix.tld'. Multi-label wildcards, full-regex, and bare suffixes are rejected as SSRF vectors.");
        });

        When(e => !string.IsNullOrWhiteSpace(e.Host), () =>
        {
            RuleFor(e => e.Host!)
                .Must(BePlainDnsLabel)
                .WithMessage("'host' must be a plain DNS host without wildcards or regex metacharacters.");
        });

        RuleFor(e => e.Schemes)
            .NotEmpty().WithMessage("'schemes' must declare at least one entry — empty schemes match nothing and are useless.");

        RuleForEach(e => e.Schemes)
            .Must(BeHttpScheme)
            .WithMessage("Scheme must be 'http' or 'https'. Other schemes are SSRF vectors and not permitted.");

        RuleFor(e => e.Ports)
            .NotEmpty().WithMessage("'ports' must declare at least one entry — empty ports match nothing and are useless.");

        RuleForEach(e => e.Ports)
            .Must(BeValidPort)
            .WithMessage($"Port must be between {MinPort} and {MaxPort} inclusive.");
    }

    private static bool HaveExactlyOneOfHostOrPattern(EgressAllowlistEntry entry)
    {
        var hasHost = !string.IsNullOrWhiteSpace(entry.Host);
        var hasPattern = !string.IsNullOrWhiteSpace(entry.HostPattern);
        return hasHost != hasPattern;
    }

    /// <summary>
    /// Mirrors <c>DefaultEgressPolicy.ValidateHostPattern</c>: pattern must start
    /// with "*." followed by a literal suffix containing at least one dot, with
    /// no further wildcards or regex metacharacters in the suffix.
    /// </summary>
    private static bool BeLeftmostLabelWildcard(string pattern)
    {
        if (!pattern.StartsWith("*.", StringComparison.Ordinal))
        {
            return false;
        }

        var suffix = pattern[2..];
        if (suffix.Length == 0 || !suffix.Contains('.', StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var ch in suffix)
        {
            // Reject anything that isn't a normal DNS label character or a dot.
            // Bans '*', '?', '[', ']', and other regex/glob characters.
            var ok = char.IsLetterOrDigit(ch) || ch == '-' || ch == '.';
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }

    private static bool BePlainDnsLabel(string host)
    {
        if (host.Length == 0)
        {
            return false;
        }

        foreach (var ch in host)
        {
            // No wildcards or regex metacharacters; only DNS-safe characters allowed.
            var ok = char.IsLetterOrDigit(ch) || ch == '-' || ch == '.';
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }

    private static bool BeHttpScheme(string scheme) =>
        string.Equals(scheme, "http", StringComparison.OrdinalIgnoreCase)
        || string.Equals(scheme, "https", StringComparison.OrdinalIgnoreCase);

    private static bool BeValidPort(int port) => port is >= MinPort and <= MaxPort;
}
