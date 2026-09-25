using Domain.Common.Config.Observability;
using FluentValidation;

namespace Application.Core.Validation;

/// <summary>
/// Validates <see cref="Agent365ExporterConfig"/>. Every rule is conditional on
/// <see cref="Agent365ExporterConfig.Enabled"/> — the exporter is OFF by default, so a host that
/// omits the section binds valid defaults and boots unchanged.
/// </summary>
/// <remarks>
/// <para>
/// When the exporter is on, the rules close failure modes that are otherwise <em>silent</em>. The
/// Agent 365 ingestion service validates the agent id in the payload against the authenticated
/// caller and needs a GUID to identify the agent at all; a malformed or missing value does not
/// produce an error the harness can observe — it produces an agent that never appears, or appears
/// unidentified, in the tenant's dashboards. Failing at startup is the only place that mistake can
/// be caught cheaply.
/// </para>
/// <para>
/// Offline storage is validated for a different reason: it is the one setting here that can write
/// conversation content to disk, so if a consumer opts into it they must name the directory rather
/// than inherit a temp-directory default chosen by a library.
/// </para>
/// <para>
/// Auto-discovered via <c>AddValidatorsFromAssembly</c> on the Application.Core assembly — no
/// manual registration required. Wired into the startup options pipeline at
/// <c>RegisterValidatedConfigSections</c> with <c>ValidateOnStart</c>. Both halves matter:
/// assembly discovery alone would register the validator without anything ever invoking it.
/// </para>
/// </remarks>
public sealed class Agent365ExporterConfigValidator : AbstractValidator<Agent365ExporterConfig>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Agent365ExporterConfigValidator"/> class.
    /// </summary>
    public Agent365ExporterConfigValidator()
    {
        When(x => x.Enabled, () =>
        {
            RuleFor(x => x.AgentAppId)
                .Must(BeAGuid)
                .WithMessage(
                    "AgentAppId must be the GUID appId of the Entra agent identity this host runs " +
                    "as (not the blueprint appId, and not the object id). Agent 365 rejects a " +
                    "payload whose agent id does not match the authenticated caller, and a " +
                    "non-GUID value makes the agent show as unidentified in the tenant's " +
                    "dashboards rather than raising an error.");

            RuleFor(x => x.TenantId)
                .Must(BeAGuid)
                .WithMessage(
                    "TenantId must be the GUID of the Entra tenant the agent identity belongs to. " +
                    "Agent identities are single-tenant and are only issued tokens in the tenant " +
                    "where they were created, so a wrong or missing tenant means every export is " +
                    "refused.");

            // Optional, so only checked when actually supplied: an unset blueprint id simply means
            // Agent 365 cannot group this agent with others of its kind, which is a lost nicety rather
            // than a failure. A malformed one is a typo worth reporting.
            //
            // Blank counts as absent, not as malformed. This repository is a template consumers clone,
            // and an empty placeholder ("BlueprintId": "") left in copied settings expresses "not
            // provided" — refusing to boot over it would be a trap rather than a useful check. Only a
            // non-blank value is a claim about a real blueprint, and only that is worth validating.
            When(x => !string.IsNullOrWhiteSpace(x.BlueprintId), () =>
            {
                RuleFor(x => x.BlueprintId)
                    .Must(BeAGuid)
                    .WithMessage(
                        "BlueprintId is set but is not a GUID. Leave it unset if the agent " +
                        "identity blueprint's appId is not known — an invalid value is not " +
                        "treated as absent.");
            });

            // Per-agent overrides. An unmatched key degrades silently to the host default, so the
            // only mistakes worth failing on are the ones that would corrupt attribution for an
            // agent that DOES match: a blank key (unmatchable), a missing or non-GUID appId, and a
            // non-GUID blueprint id.
            RuleForEach(x => x.Agents)
                .Must(entry => !string.IsNullOrWhiteSpace(entry.Key))
                .WithMessage(
                    "Agents contains an entry with a blank agent name. The key is matched against " +
                    "the running agent's name, so a blank key can never match and the override is " +
                    "dead configuration.")
                .Must(entry => BeAGuid(entry.Value?.AppId))
                .WithMessage((_, entry) =>
                    $"Agents entry '{entry.Key}' must set AppId to the GUID appId of that agent's " +
                    "own Entra agent identity. Remove the entry to let the agent fall back to the " +
                    "host-level AgentAppId.")
                // Blank counts as absent here too, for the same template reason as the host-level id.
                .Must(entry => string.IsNullOrWhiteSpace(entry.Value?.BlueprintId)
                    || BeAGuid(entry.Value!.BlueprintId))
                .WithMessage((_, entry) =>
                    $"Agents entry '{entry.Key}' sets BlueprintId but it is not a GUID. Leave it " +
                    "unset if the blueprint appId is not known — an invalid value is not treated " +
                    "as absent.");

            // Two keys differing only by case would both match the same running agent, and which one
            // won would depend on enumeration order — so the agent's identity in the tenant's
            // governance records would be effectively arbitrary. Rejected at startup rather than
            // resolved by a tie-break rule, because there is no correct answer to pick.
            // Compared without trimming, matching the lookup exactly: it compares the configured key to
            // the agent id as-is, so a key with stray whitespace is a key that never matches anything
            // rather than a duplicate of one that does.
            RuleFor(x => x.Agents)
                .Must(agents => agents is null
                    || agents.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() == agents.Count)
                .WithMessage(
                    "Agents contains two entries whose names differ only by case. Agent names are "
                    + "matched case-insensitively, so both would match the same agent and which "
                    + "identity it reported would depend on ordering. Remove the duplicate.");

            When(x => x.EnableOfflineStorage, () =>
            {
                RuleFor(x => x.OfflineStorageDirectory)
                    .NotEmpty()
                    .WithMessage(
                        "OfflineStorageDirectory is required when EnableOfflineStorage is set. " +
                        "Undelivered spans can contain prompts, tool arguments and model output, " +
                        "so the harness will not fall back to the SDK's per-user temp directory " +
                        "default — name a location this deployment controls, with owner-only " +
                        "permissions.");
            });
        });
    }

    // Guid.TryParse rather than a format string: Agent 365 accepts the appId in the ordinary
    // registered-application form, and the harness should not be stricter about punctuation than
    // the service it is talking to.
    private static bool BeAGuid(string? value)
        => !string.IsNullOrWhiteSpace(value) && Guid.TryParse(value.Trim(), out _);
}
