using Application.AI.Common.Interfaces.ClaimVerification;
using Application.Common.Helpers;
using Domain.Common.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Verification.Readers;

/// <summary>
/// The <c>"config"</c>-scheme <see cref="ILocatedArtifactReader"/>: resolves a
/// <c>"config:AI.Resilience.Retry.MaxAttempts"</c>-shaped location by walking that dotted path
/// against the live <see cref="AppConfig"/> snapshot via public-property reflection, and returns
/// the live value as evidence text.
/// </summary>
/// <remarks>
/// <para>
/// The first consumer of this scheme is <c>HarnessChangeSuggestion.CurrentValue</c> — a model's
/// claim about what a config field currently holds, checked against what it actually holds via
/// <see cref="Application.AI.Common.Services.SkillTraining.ConfigSurfaceConstraint.ResolveConfigPath"/>.
/// </para>
/// <para>
/// <see cref="AppConfig"/> carries live secrets (provider API keys, connection strings, HMAC keys,
/// bearer tokens) alongside ordinary settings, all as public instance properties — reflection with
/// no filter would make every one of them readable via a crafted <c>Claim.Location</c>. This is a
/// model-supplied field, so <see cref="AllowedPaths"/> is a fixed, minimal, code-owned allowlist
/// deliberately separate from (not derived from) <c>ConfigSurfaceConstraint</c>'s own — that
/// constraint governs which fields a suggestion may propose <em>changing</em>, a different question
/// than which fields are safe to expose as claim-verification evidence, and coupling the two would
/// let a future widening of one accidentally widen the other. Only paths named here resolve; every
/// other path — including a perfectly well-formed one — is refused the same way a nonexistent one
/// would be, so this reader's refusal never distinguishes "not allowed" from "not found."
/// </para>
/// </remarks>
public sealed class ConfigSnapshotLocatedArtifactReader : ILocatedArtifactReader
{
    private const string SchemePrefix = "config:";

    private static readonly IReadOnlySet<string> AllowedPaths =
        new HashSet<string>(StringComparer.Ordinal) { "AI.Resilience.Retry.MaxAttempts" };

    private readonly IOptionsMonitor<AppConfig> _config;
    private readonly ILogger<ConfigSnapshotLocatedArtifactReader> _logger;

    /// <summary>Initializes a new instance of the <see cref="ConfigSnapshotLocatedArtifactReader"/> class.</summary>
    public ConfigSnapshotLocatedArtifactReader(IOptionsMonitor<AppConfig> config, ILogger<ConfigSnapshotLocatedArtifactReader> logger)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);
        _config = config;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<string?> TryReadAsync(string location, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(location);

        if (!location.StartsWith(SchemePrefix, StringComparison.Ordinal))
        {
            return Task.FromResult<string?>(null);
        }

        var path = location[SchemePrefix.Length..];
        if (string.IsNullOrWhiteSpace(path))
        {
            _logger.LogWarning("Malformed config location '{Location}' — no path after the 'config:' prefix.", location);
            return Task.FromResult<string?>(null);
        }

        if (!AllowedPaths.Contains(path))
        {
            _logger.LogWarning(
                "Config location '{Path}' is not on the claim-verification allowlist; refusing to read it.", path);
            return Task.FromResult<string?>(null);
        }

        var value = ReflectionHelper.GetPropertyValue(_config.CurrentValue, path);
        if (value is null)
        {
            _logger.LogWarning(
                "Allowlisted config location '{Path}' did not resolve against the live AppConfig snapshot — the allowlist entry may have drifted from the config schema.",
                path);
            return Task.FromResult<string?>(null);
        }

        return Task.FromResult<string?>($"{path} = {value}");
    }
}
