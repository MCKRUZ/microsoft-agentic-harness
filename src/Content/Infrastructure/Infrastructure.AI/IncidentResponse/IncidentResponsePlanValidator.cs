using Domain.Common.Helpers;
using Domain.AI.Governance;
using Domain.Common.Config;
using Domain.Common.Config.AI.IncidentResponse;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.IncidentResponse;

/// <summary>
/// One-shot startup validator for the incident-response plan registry. Runs
/// once via <see cref="IHostedService.StartAsync"/> and refuses to boot the
/// host when the configured plans are internally inconsistent.
/// </summary>
/// <remarks>
/// <para>
/// Validation rules (only when at least one plan is configured):
/// </para>
/// <list type="number">
///   <item><description>
///     <b>Required fields</b> — every plan must declare a non-empty
///     <see cref="IncidentResponsePlan.Name"/> and
///     <see cref="IncidentResponsePlan.IncidentType"/>. An unnamed plan can't
///     be referenced from <see cref="IncidentResponsePlanConfig.DefaultPlanName"/>
///     and can't appear in audit lines coherently.
///   </description></item>
///   <item><description>
///     <b>Unique names</b> — two plans sharing the same
///     <see cref="IncidentResponsePlan.Name"/> make audit lines ambiguous about
///     which plan was applied.
///   </description></item>
///   <item><description>
///     <b>Unique incident types</b> — two plans sharing the same
///     <see cref="IncidentResponsePlan.IncidentType"/> mean only the first
///     would ever fire; the second is dead config and almost certainly a
///     mistake.
///   </description></item>
///   <item><description>
///     <b>Known autonomy tier override</b> —
///     <see cref="IncidentResponsePlan.AutonomyTierOverride"/>, when non-null,
///     must parse to an <see cref="AutonomyLevel"/> enum value. A typo would
///     otherwise silently fall back to "no override" at the consumption site.
///   </description></item>
///   <item><description>
///     <b>Default plan exists</b> —
///     <see cref="IncidentResponsePlanConfig.DefaultPlanName"/>, when non-null,
///     must name an actually-declared plan. Otherwise the fallback path is
///     dead.
///   </description></item>
/// </list>
/// <para>
/// When no plans are configured the validator no-ops — an empty registry is a
/// legitimate "incident response not in use" deployment.
/// </para>
/// </remarks>
public sealed class IncidentResponsePlanValidator : IHostedService
{
    private readonly IServiceProvider _services;
    private readonly IOptionsMonitor<AppConfig> _config;
    private readonly ILogger<IncidentResponsePlanValidator> _logger;

    /// <summary>Initializes a new <see cref="IncidentResponsePlanValidator"/>.</summary>
    /// <remarks>
    /// <see cref="IHostEnvironment"/> is resolved lazily from
    /// <see cref="IServiceProvider"/> inside <see cref="StartAsync"/> to match
    /// the <c>ChangeProposalStartupValidator</c> pattern — DI tests that
    /// enumerate hosted services without registering a host environment don't
    /// fail at materialization. The incident-response validator does not vary
    /// behaviour by environment today; the parameter is reserved for future
    /// rules (e.g. "warn rather than throw under Development").
    /// </remarks>
    public IncidentResponsePlanValidator(
        IServiceProvider services,
        IOptionsMonitor<AppConfig> config,
        ILogger<IncidentResponsePlanValidator> logger)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);

        _services = services;
        _config = config;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var cfg = _config.CurrentValue.AI.IncidentResponse;
        if (cfg.Plans.Count == 0)
        {
            // No plans configured — nothing to validate. A null DefaultPlanName
            // is also a no-op; a non-null DefaultPlanName with zero plans is
            // an error the consumer should see.
            if (!string.IsNullOrWhiteSpace(cfg.DefaultPlanName))
            {
                throw new InvalidOperationException(
                    "AppConfig.AI.IncidentResponse.DefaultPlanName is set to " +
                    $"'{cfg.DefaultPlanName}' but no plans are configured. " +
                    "Either remove the default name or declare at least one plan.");
            }
            return Task.CompletedTask;
        }

        // Probe IHostEnvironment so the lazy resolution exercises uniformly with
        // the ChangeProposal validator; not currently used by any rule.
        _ = _services.GetService<IHostEnvironment>();

        ValidatePlans(cfg);
        ValidateDefaultPlanName(cfg);

        _logger.LogInformation(
            "IncidentResponse plan registry validated: {PlanCount} plan(s); default='{DefaultPlanName}'.",
            cfg.Plans.Count,
            cfg.DefaultPlanName ?? "(none)");

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static void ValidatePlans(IncidentResponsePlanConfig cfg)
    {
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < cfg.Plans.Count; i++)
        {
            var plan = cfg.Plans[i];

            if (string.IsNullOrWhiteSpace(plan.Name))
            {
                throw new InvalidOperationException(
                    $"AppConfig.AI.IncidentResponse.Plans[{i}].Name is required.");
            }

            if (string.IsNullOrWhiteSpace(plan.IncidentType))
            {
                throw new InvalidOperationException(
                    $"AppConfig.AI.IncidentResponse.Plans[{i}].IncidentType is required " +
                    $"(plan '{plan.Name}').");
            }

            if (!seenNames.Add(plan.Name))
            {
                throw new InvalidOperationException(
                    "AppConfig.AI.IncidentResponse.Plans contains duplicate Name " +
                    $"'{plan.Name}'. Plan names must be unique; the audit trail " +
                    "records the name of the plan that drove a gate overlay, and " +
                    "duplicates make the audit ambiguous.");
            }

            if (!seenTypes.Add(plan.IncidentType))
            {
                throw new InvalidOperationException(
                    "AppConfig.AI.IncidentResponse.Plans contains duplicate IncidentType " +
                    $"'{plan.IncidentType}' (plan '{plan.Name}'). The resolver picks the " +
                    "first match deterministically, so the second plan would be dead " +
                    "config; flagging at boot.");
            }

            // Name-only. This is a boot validator for a tier override, and a bare Enum.TryParse
            // accepted "2" and "Restricted,Autonomous" — so the one check standing between a typo
            // and a widened autonomy tier passed the two forms most likely to widen it. The switch
            // also makes this case-insensitive, matching how AutonomyLevel is read everywhere else.
            //
            // Note this setting is currently validated and never applied: AutonomyTierOverride is
            // read by nothing outside this validator and the config POCO, despite the plan model
            // documenting it as forcing the tier for the incident's duration. Validating it is still
            // right — it must not start meaning something different the day it IS wired up.
            if (plan.AutonomyTierOverride is not null
                && !EnumNameHelper.TryParseName<AutonomyLevel>(plan.AutonomyTierOverride, out _))
            {
                var known = string.Join(", ", Enum.GetNames<AutonomyLevel>());
                throw new InvalidOperationException(
                    $"AppConfig.AI.IncidentResponse.Plans[{i}].AutonomyTierOverride " +
                    $"'{plan.AutonomyTierOverride}' (plan '{plan.Name}') does not match " +
                    $"any known AutonomyLevel value. Allowed values: {known}.");
            }
        }
    }

    private static void ValidateDefaultPlanName(IncidentResponsePlanConfig cfg)
    {
        if (string.IsNullOrWhiteSpace(cfg.DefaultPlanName))
        {
            return;
        }

        for (var i = 0; i < cfg.Plans.Count; i++)
        {
            if (string.Equals(cfg.Plans[i].Name, cfg.DefaultPlanName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        throw new InvalidOperationException(
            $"AppConfig.AI.IncidentResponse.DefaultPlanName '{cfg.DefaultPlanName}' " +
            "does not match the Name of any declared plan. Either remove the default " +
            "name or rename a plan to match it.");
    }
}
