using Application.AI.Common.Exceptions;
using Domain.AI.Skills;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Skills;

/// <summary>
/// Egress-manifest validation half of <see cref="SkillMetadataParser"/> — split out to keep the
/// main file under this repo's 400-line convention (#531).
/// </summary>
public sealed partial class SkillMetadataParser
{
    /// <summary>
    /// Validates a non-null <paramref name="egress"/> manifest against the same SSRF-narrow rules
    /// the runtime egress policy applies (#531), throwing <see cref="SkillParsingException"/> if it
    /// fails — a malformed manifest must never reach the policy resolver. Mirrors
    /// <see cref="ScanOrRefuse"/>'s "refuse to construct the definition" shape for a different
    /// class of manifest defect (schema validity, not content safety).
    /// </summary>
    private void ValidateEgressOrRefuse(EgressManifest? egress, string skillFilePath)
    {
        if (egress is null)
            return;

        var result = _egressValidator.Validate(egress);
        if (result.IsValid)
            return;

        var reason = string.Join("; ", result.Errors.Select(e => e.ErrorMessage));
        _logger.LogWarning(
            "Refusing skill manifest at {Path}: invalid egress allowlist: {Reason}", skillFilePath, reason);
        throw new SkillParsingException(skillFilePath, $"Invalid egress manifest: {reason}");
    }
}
