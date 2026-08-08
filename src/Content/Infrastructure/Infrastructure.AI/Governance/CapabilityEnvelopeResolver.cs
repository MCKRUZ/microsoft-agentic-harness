using System.Security.Claims;
using Application.AI.Common.Interfaces.Governance;
using Application.Common.Helpers;
using Domain.AI.Bundles;
using Domain.AI.Governance;
using Domain.Common.Config;
using Domain.Common.Config.AI.BundleExecution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Governance;

/// <summary>
/// Resolves a caller's <see cref="CapabilityEnvelope"/> from the configured per-caller grant table
/// (<c>AppConfig:AI:BundleExecution:Envelopes</c>). Precedence is exact subject claim → least-privilege
/// combination of matching roles → configured default.
/// </summary>
/// <remarks>
/// <para>
/// The subject claim (<see cref="ClaimTypes.NameIdentifier"/> or the raw <c>sub</c> claim) is the most
/// specific identity, so an exact subject grant wins outright. When no subject grant matches, the caller's
/// roles are consulted: because roles are additive on a principal but a security grant must never widen by
/// accident, several matching roles are combined to the <em>least-privilege</em> result — the intersection
/// of their tool and MCP allowlists and the minimum of their autonomy ceilings. When neither a subject nor
/// any role matches, the configured default applies, which is itself fail-closed unless an operator has
/// widened it.
/// </para>
/// <para>
/// The result is always non-null: there is no path on which an unmatched caller silently escapes the
/// envelope. An unrecognised <see cref="CapabilityEnvelopeConfig.AutonomyCeiling"/> string degrades to the
/// most restrictive tier with a warning rather than opening up.
/// </para>
/// </remarks>
public sealed class CapabilityEnvelopeResolver : ICapabilityEnvelopeResolver
{
    private readonly IOptionsMonitor<AppConfig> _options;
    private readonly ILogger<CapabilityEnvelopeResolver> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="CapabilityEnvelopeResolver"/> class.
    /// </summary>
    /// <param name="options">Application configuration providing the per-caller envelope table.</param>
    /// <param name="logger">Logger for invalid-autonomy-ceiling warnings.</param>
    public CapabilityEnvelopeResolver(
        IOptionsMonitor<AppConfig> options,
        ILogger<CapabilityEnvelopeResolver> logger)
    {
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public CapabilityEnvelope Resolve(ClaimsPrincipal? principal)
    {
        var envelopes = _options.CurrentValue.AI.BundleExecution.Envelopes;

        if (principal is not null)
        {
            var subject = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value
                          ?? principal.FindFirst("sub")?.Value;

            if (!string.IsNullOrEmpty(subject) && envelopes.BySubject.TryGetValue(subject, out var bySubject))
                return WarnIfMcpGrantIsUnusable(Map(bySubject));

            var matchingRoles = RoleValues(principal)
                .Where(role => envelopes.ByRole.ContainsKey(role))
                .Select(role => envelopes.ByRole[role])
                .ToList();

            if (matchingRoles.Count > 0)
                return WarnIfMcpGrantIsUnusable(CombineLeastPrivilege(matchingRoles));
        }

        return WarnIfMcpGrantIsUnusable(Map(envelopes.Default));
    }

    /// <summary>
    /// Warns when an envelope grants MCP servers but names no tools, and returns it unchanged.
    /// </summary>
    /// <remarks>
    /// <para>
    /// That shape is almost certainly an operator error rather than a deliberate grant.
    /// <see cref="CapabilityEnvelope.AllowedMcpServers"/> gates which servers may be <em>contacted</em>;
    /// invoking any tool one of them publishes still requires that tool's name in
    /// <see cref="CapabilityEnvelope.AllowedTools"/>. So the run pays to reach every granted server, the
    /// fetched schemas are published to the model, the model calls one — and the permission resolver
    /// denies it, because the name is outside the allowlist. The operator sees a bundle that fails every
    /// call for no visible reason.
    /// </para>
    /// <para>
    /// This warns rather than throwing: the configuration is safe (it fails closed, which is the correct
    /// direction) and an empty tool list is legitimate for a caller who is meant to have no tool access
    /// at all. Refusing to start would turn a misconfiguration into an outage.
    /// </para>
    /// </remarks>
    private CapabilityEnvelope WarnIfMcpGrantIsUnusable(CapabilityEnvelope envelope)
    {
        if (envelope.AllowedMcpServers.Count > 0 && envelope.AllowedTools.Count == 0)
        {
            _logger.LogWarning(
                "Capability envelope grants {ServerCount} MCP server(s) ({Servers}) but names no tools in " +
                "AllowedTools, so every tool those servers publish will be denied at invocation. " +
                "AllowedMcpServers gates which servers may be CONTACTED; AllowedTools gates which tools may " +
                "be INVOKED — list each MCP tool name in AllowedTools as well.",
                envelope.AllowedMcpServers.Count,
                string.Join(", ", envelope.AllowedMcpServers));
        }

        return envelope;
    }

    /// <summary>
    /// The caller's role values, drawn from the standard <see cref="ClaimTypes.Role"/> claim and the common
    /// short <c>roles</c> / <c>role</c> claim names. Hosts whose token pipeline does not map roles onto the
    /// long SOAP claim URI (e.g. Azure AD app roles emitted as a <c>roles</c> claim) would otherwise see
    /// their <c>ByRole</c> grants become dead config — mirrors the subject path's <c>sub</c> fallback.
    /// </summary>
    private static IEnumerable<string> RoleValues(ClaimsPrincipal principal) => principal.Claims
        .Where(c => c.Type is ClaimTypes.Role or "roles" or "role")
        .Select(c => c.Value);

    /// <summary>Projects a bindable config grant into the immutable domain envelope.</summary>
    private CapabilityEnvelope Map(CapabilityEnvelopeConfig config) => new()
    {
        AllowedTools = [.. config.AllowedTools],
        AllowedMcpServers = [.. config.AllowedMcpServers],
        AutonomyCeiling = ParseCeiling(config.AutonomyCeiling)
    };

    /// <summary>
    /// Combines several role grants into the least-privilege envelope: the intersection of their tool and
    /// MCP allowlists and the minimum (most restrictive) autonomy ceiling. Overlapping roles can therefore
    /// only ever narrow the grant, never widen it.
    /// </summary>
    private CapabilityEnvelope CombineLeastPrivilege(IReadOnlyList<CapabilityEnvelopeConfig> configs)
    {
        var mapped = configs.Select(Map).ToList();

        return new CapabilityEnvelope
        {
            AllowedTools = IntersectAll(mapped.Select(m => m.AllowedTools)),
            AllowedMcpServers = IntersectAll(mapped.Select(m => m.AllowedMcpServers)),
            AutonomyCeiling = mapped.Min(m => m.AutonomyCeiling)
        };
    }

    /// <summary>
    /// Case-insensitive intersection of a sequence of name lists. Returns the names present in every list.
    /// </summary>
    private static IReadOnlyList<string> IntersectAll(IEnumerable<IReadOnlyList<string>> lists)
    {
        HashSet<string>? acc = null;

        foreach (var list in lists)
        {
            if (acc is null)
            {
                acc = new HashSet<string>(list, StringComparer.OrdinalIgnoreCase);
            }
            else
            {
                acc.IntersectWith(list);
            }
        }

        return acc is null ? [] : [.. acc];
    }

    private AutonomyLevel ParseCeiling(string value)
    {
        // Match only against the defined tier NAMES, case-insensitively. Enum.TryParse would also accept a
        // numeric string ("2") or a comma-composite ("Restricted,Autonomous" -> 2) — either of which would
        // silently WIDEN a config typo to full autonomy. An unrecognised value must degrade closed, so we
        // reject anything that is not an exact tier name. This was hand-rolled here before the shared
        // helper existed; it now defers to EnumNameHelper so every governance parse enforces one rule.
        if (EnumNameHelper.TryParseName<AutonomyLevel>(value, out var ceiling))
            return ceiling;

        _logger.LogWarning(
            "Capability envelope: invalid AutonomyCeiling '{Ceiling}', falling back to the most restrictive tier (Restricted)",
            value);

        return AutonomyLevel.Restricted;
    }
}
