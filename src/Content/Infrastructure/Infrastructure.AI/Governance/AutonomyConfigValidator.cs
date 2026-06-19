using Domain.AI.Changes;
using Domain.AI.Governance;
using Domain.Common.Config;
using Domain.Common.Config.AI.Permissions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Governance;

/// <summary>
/// One-shot startup validator for the PR-4 graded-autonomy configuration. Refuses
/// to boot the host when <see cref="GradedAutonomyConfig.Enabled"/> is true and
/// the config is internally inconsistent — e.g. a Production environment row
/// declaring <see cref="BlastRadius.Critical"/> as <see cref="AutonomyDecision.AutoApprove"/>
/// (which the Domain-level safety check would override at runtime but should never
/// have been allowed to bind in the first place).
/// </summary>
/// <remarks>
/// <para>
/// Checks performed (only when <c>GradedAutonomy.Enabled</c> is true):
/// </para>
/// <list type="number">
///   <item><description>
///     <b>Unknown decision or radius names</b> — every row's <c>Decision</c> and
///     row key must parse to <see cref="AutonomyDecision"/> / <see cref="BlastRadius"/>.
///     Typos in config would otherwise produce silent fallbacks to RequiresApproval,
///     which is safe at runtime but indistinguishable from intent — louder at boot.
///   </description></item>
///   <item><description>
///     <b>Critical-AutoApprove rows</b> — any row mapping <see cref="BlastRadius.Critical"/>
///     to <see cref="AutonomyDecision.AutoApprove"/> at any scope (environment or skill)
///     is rejected. The Domain policy double-guards this at runtime, but accepting it
///     here would let a misconfig pass review.
///   </description></item>
///   <item><description>
///     <b>Production permissive rows</b> — Production environment rows declaring
///     <see cref="BlastRadius.High"/> as AutoApprove are rejected. The runtime
///     policy still works without this guard, but Production at High blast radius
///     is the canonical "load-bearing change" case where auto-approve is almost
///     certainly a misconfiguration.
///   </description></item>
///   <item><description>
///     <b>Per-skill tier widens baseline</b> — a per-skill <c>Tier</c> that is
///     looser than the configured <see cref="PermissionsConfig.DefaultAutonomyLevel"/>
///     is rejected. The evaluator silently downgrades these at runtime; rejecting at
///     boot makes the intent explicit.
///   </description></item>
/// </list>
/// <para>
/// When graded autonomy is disabled the validator no-ops — the entire layer is
/// inert anyway, and consumers exploring the template shouldn't be blocked from
/// running the host.
/// </para>
/// </remarks>
public sealed class AutonomyConfigValidator : IHostedService
{
    private readonly IServiceProvider _services;
    private readonly IOptionsMonitor<AppConfig> _config;
    private readonly ILogger<AutonomyConfigValidator> _logger;

    /// <summary>Initializes a new <see cref="AutonomyConfigValidator"/>.</summary>
    /// <remarks>
    /// <see cref="IHostEnvironment"/> is resolved lazily from
    /// <see cref="IServiceProvider"/> inside <see cref="StartAsync"/> rather than
    /// required at construction so DI tests that enumerate
    /// <c>GetServices&lt;IHostedService&gt;()</c> without a registered host
    /// environment don't fail at materialization. Real hosts always register
    /// <see cref="IHostEnvironment"/>.
    /// </remarks>
    public AutonomyConfigValidator(
        IServiceProvider services,
        IOptionsMonitor<AppConfig> config,
        ILogger<AutonomyConfigValidator> logger)
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
        var permissions = _config.CurrentValue.AI.Permissions;
        var graded = permissions.GradedAutonomy;
        if (!graded.Enabled)
        {
            return Task.CompletedTask;
        }

        var environment = _services.GetService<IHostEnvironment>();
        var environmentName = environment?.EnvironmentName ?? "Unknown";

        var errors = new List<string>();

        ValidateDefaultAutonomyLevel(permissions, errors);
        ValidatePerEnvironment(graded, environmentName, errors);
        ValidatePerSkill(graded, permissions, errors);
        ValidateStateChangerOptIns(graded, errors);

        if (errors.Count == 0)
        {
            _logger.LogInformation(
                "Graded autonomy configuration validated successfully ({EnvCount} environment rows, " +
                "{SkillCount} skill rows, {OptInCount} state-changer opt-ins).",
                graded.PerEnvironment.Count, graded.PerSkill.Count, graded.StateChangerOptIns.Count);
            return Task.CompletedTask;
        }

        var message =
            "Graded autonomy configuration is internally inconsistent and refuses to boot. " +
            "Fix the following issues in AppConfig.AI.Permissions.GradedAutonomy then restart:\n - "
            + string.Join("\n - ", errors);
        throw new InvalidOperationException(message);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static void ValidateDefaultAutonomyLevel(PermissionsConfig permissions, List<string> errors)
    {
        // The tool-risk gate (ToolPermissionBehavior) parses this value at runtime; an invalid
        // tier would silently disable the gate. Fail fast at boot instead — but only when graded
        // autonomy is enabled (this validator returns early otherwise), so a typo cannot quietly
        // weaken governance.
        if (!Enum.TryParse<AutonomyLevel>(permissions.DefaultAutonomyLevel, ignoreCase: true, out _))
        {
            errors.Add(
                $"DefaultAutonomyLevel '{permissions.DefaultAutonomyLevel}' is not a valid AutonomyLevel. " +
                "Valid values: Restricted, Supervised, Autonomous.");
        }
    }

    private static void ValidatePerEnvironment(
        GradedAutonomyConfig graded,
        string currentEnvironmentName,
        List<string> errors)
    {
        foreach (var (envName, envConfig) in graded.PerEnvironment)
        {
            foreach (var (radiusName, ruleConfig) in envConfig.PerBlastRadius)
            {
                if (!Enum.TryParse<BlastRadius>(radiusName, ignoreCase: true, out var radius))
                {
                    errors.Add(
                        $"PerEnvironment[{envName}] declares unknown BlastRadius '{radiusName}'. " +
                        "Valid values: Trivial, Low, Medium, High, Critical.");
                    continue;
                }

                if (!Enum.TryParse<AutonomyDecision>(ruleConfig.Decision, ignoreCase: true, out var decision))
                {
                    errors.Add(
                        $"PerEnvironment[{envName}][{radiusName}] declares unknown Decision '{ruleConfig.Decision}'. " +
                        "Valid values: AutoApprove, RequiresApproval, Forbidden.");
                    continue;
                }

                if (radius == BlastRadius.Critical && decision == AutonomyDecision.AutoApprove)
                {
                    errors.Add(
                        $"PerEnvironment[{envName}] maps BlastRadius.Critical to AutoApprove — Critical " +
                        "must always require human approval. Remove the row or change Decision to RequiresApproval.");
                }

                if (IsProductionLike(envName) && radius == BlastRadius.High
                    && decision == AutonomyDecision.AutoApprove)
                {
                    errors.Add(
                        $"PerEnvironment[{envName}] maps BlastRadius.High to AutoApprove in a Production-like " +
                        "environment — High-blast-radius changes in Production are load-bearing and require " +
                        "approval. Either lower the environment match or change Decision to RequiresApproval.");
                }
            }
        }

        _ = currentEnvironmentName; // Reserved for environment-conditional checks; not used today.
    }

    private static void ValidatePerSkill(
        GradedAutonomyConfig graded,
        PermissionsConfig permissions,
        List<string> errors)
    {
        if (!Enum.TryParse<AutonomyLevel>(permissions.DefaultAutonomyLevel, ignoreCase: true, out var baseline))
        {
            baseline = AutonomyLevel.Supervised;
        }

        foreach (var (skillKey, skillConfig) in graded.PerSkill)
        {
            if (skillConfig.Tier is not null)
            {
                if (!Enum.TryParse<AutonomyLevel>(skillConfig.Tier, ignoreCase: true, out var skillTier))
                {
                    errors.Add(
                        $"PerSkill[{skillKey}].Tier '{skillConfig.Tier}' is not a valid AutonomyLevel. " +
                        "Valid values: Restricted, Supervised, Autonomous.");
                }
                else if (skillTier > baseline)
                {
                    errors.Add(
                        $"PerSkill[{skillKey}].Tier '{skillTier}' is looser than the baseline " +
                        $"DefaultAutonomyLevel '{baseline}'. Per-skill tiers may only narrow the baseline. " +
                        "Either remove the per-skill tier or set it to Restricted/Supervised.");
                }
            }

            foreach (var (radiusName, ruleConfig) in skillConfig.PerBlastRadius)
            {
                if (!Enum.TryParse<BlastRadius>(radiusName, ignoreCase: true, out var radius))
                {
                    errors.Add(
                        $"PerSkill[{skillKey}] declares unknown BlastRadius '{radiusName}'. " +
                        "Valid values: Trivial, Low, Medium, High, Critical.");
                    continue;
                }

                if (!Enum.TryParse<AutonomyDecision>(ruleConfig.Decision, ignoreCase: true, out var decision))
                {
                    errors.Add(
                        $"PerSkill[{skillKey}][{radiusName}] declares unknown Decision '{ruleConfig.Decision}'.");
                    continue;
                }

                if (radius == BlastRadius.Critical && decision == AutonomyDecision.AutoApprove)
                {
                    errors.Add(
                        $"PerSkill[{skillKey}] maps BlastRadius.Critical to AutoApprove — Critical must always " +
                        "require human approval regardless of skill.");
                }
            }
        }
    }

    private static void ValidateStateChangerOptIns(GradedAutonomyConfig graded, List<string> errors)
    {
        // A skill in StateChangerOptIns without a corresponding PerSkill rule that
        // declares AllowAutoApproveForStateChange is harmless (the opt-in alone
        // grants nothing). The inverse — a PerSkill rule with
        // AllowAutoApproveForStateChange=true but no entry in StateChangerOptIns
        // — is also harmless (the dual-key check denies it). Both are documented
        // as "intentional config noise" rather than errors. The only thing worth
        // flagging is empty-string entries.
        var blanks = graded.StateChangerOptIns.Count(s => string.IsNullOrWhiteSpace(s));
        if (blanks > 0)
        {
            errors.Add(
                $"StateChangerOptIns contains {blanks} blank entry — every entry must be a non-empty skill key.");
        }
    }

    private static bool IsProductionLike(string environmentName)
        => environmentName.Equals("Production", StringComparison.OrdinalIgnoreCase)
        || environmentName.Equals("Prod", StringComparison.OrdinalIgnoreCase);
}
