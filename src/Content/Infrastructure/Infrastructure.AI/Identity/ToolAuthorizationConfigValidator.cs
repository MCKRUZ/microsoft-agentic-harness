using Application.AI.Common.Interfaces.Identity;
using Domain.Common.Config;
using Domain.Common.Config.AI.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Identity;

/// <summary>
/// One-shot startup validator for per-agent tool authorization. Refuses to boot when
/// <see cref="ToolAuthorizationConfig.Enabled"/> is set but the surrounding configuration
/// cannot produce anything other than a blanket denial.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why a startup check rather than runtime tolerance.</strong> The gate this guards is
/// fail-closed on purpose: with no identity, no validator, or no allowlist, every tool call is
/// refused. Each of those is a configuration mistake rather than a policy, and discovering it at
/// runtime means an agent that appears to start normally and then declines to do anything, with
/// the reason visible only to whoever is reading structured logs. Boot is the honest place to
/// say so.
/// </para>
/// <para>
/// Checks performed, all only when the feature is switched on:
/// </para>
/// <list type="number">
///   <item><description>
///     <b>Identity subsystem is off</b> — with <see cref="AgentIdentityConfig.Enabled"/> false
///     there is no workload identity to authorize against, so every call would be denied.
///   </description></item>
///   <item><description>
///     <b>No allowlist</b> — an empty <see cref="ToolAuthorizationConfig.AllowedToolsByAgentId"/>
///     denies every agent every tool. That is the correct fail-closed reading of an empty policy
///     and is almost never what the operator meant by switching the feature on.
///   </description></item>
///   <item><description>
///     <b>Blank agent ids or tool keys</b> — an empty key can never match a resolved identity or
///     a registered tool, so it is silently dead policy.
///   </description></item>
///   <item><description>
///     <b>Missing services</b> — the feature is switched on but the Infrastructure identity
///     registrations are absent, so the gate would deny everything for want of a policy oracle.
///   </description></item>
/// </list>
/// <para>
/// An agent mapped to a deliberately <em>empty</em> list is not an error — that is the documented
/// way to keep an agent in the policy while granting it nothing — but it is logged, because the
/// shape is indistinguishable from an unfinished edit.
/// </para>
/// </remarks>
public sealed class ToolAuthorizationConfigValidator : IHostedService
{
    private readonly IServiceProvider _services;
    private readonly IOptionsMonitor<AppConfig> _config;
    private readonly ILogger<ToolAuthorizationConfigValidator> _logger;

    /// <summary>Initializes a new instance of the <see cref="ToolAuthorizationConfigValidator"/> class.</summary>
    /// <param name="services">
    /// Resolves the identity services lazily rather than requiring them at construction, so DI tests
    /// that enumerate hosted services without the full identity graph still materialize.
    /// </param>
    /// <param name="config">Supplies the identity and tool-authorization configuration.</param>
    /// <param name="logger">Records the validated policy shape, and any non-fatal oddities in it.</param>
    public ToolAuthorizationConfigValidator(
        IServiceProvider services,
        IOptionsMonitor<AppConfig> config,
        ILogger<ToolAuthorizationConfigValidator> logger)
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
        var identity = _config.CurrentValue.AI?.Identity;
        var toolAuthorization = identity?.ToolAuthorization;
        if (toolAuthorization is not { Enabled: true })
            return Task.CompletedTask;

        var errors = new List<string>();

        if (identity is not { Enabled: true })
        {
            errors.Add(
                "AI.Identity.ToolAuthorization.Enabled is true but AI.Identity.Enabled is false. "
                + "Per-agent tool authorization needs a workload identity to authorize; with the "
                + "identity subsystem off every tool call would be refused. Enable AI.Identity or "
                + "turn tool authorization off.");
        }

        ValidateAllowlist(toolAuthorization, errors);
        ValidateRegistrations(errors);

        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                "Per-agent tool authorization is enabled but cannot admit any call as configured, so "
                + "the host refuses to boot. Fix the following in AppConfig.AI.Identity then restart:\n - "
                + string.Join("\n - ", errors));
        }

        var emptyAllowlists = toolAuthorization.AllowedToolsByAgentId.Count(a => a.Value is { Count: 0 });
        if (emptyAllowlists > 0)
        {
            _logger.LogWarning(
                "Per-agent tool authorization has {EmptyCount} agent(s) mapped to an empty allowlist; "
                + "each is denied every tool. This is valid policy but is also what an unfinished edit "
                + "looks like.",
                emptyAllowlists);
        }

        _logger.LogInformation(
            "Per-agent tool authorization validated: {AgentCount} agent(s) in the allowlist.",
            toolAuthorization.AllowedToolsByAgentId.Count);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static void ValidateAllowlist(ToolAuthorizationConfig toolAuthorization, List<string> errors)
    {
        if (toolAuthorization.AllowedToolsByAgentId.Count == 0)
        {
            errors.Add(
                "AI.Identity.ToolAuthorization.AllowedToolsByAgentId is empty. An empty allowlist "
                + "denies every agent every tool. Name at least one agent id, or turn tool "
                + "authorization off.");
            return;
        }

        foreach (var (agentId, tools) in toolAuthorization.AllowedToolsByAgentId)
        {
            if (string.IsNullOrWhiteSpace(agentId))
            {
                errors.Add(
                    "AllowedToolsByAgentId contains a blank agent id, which can never match a "
                    + "resolved identity.");
                continue;
            }

            // `"agent-1": null` in appsettings.json binds to a null list. Reported rather than
            // dereferenced: the whole point of this validator is to replace an unexplained crash with
            // a sentence naming the setting, and an NullReferenceException out of the config validator
            // is the least helpful of all possible outcomes.
            if (tools is null)
            {
                errors.Add(
                    $"AllowedToolsByAgentId['{agentId}'] is null. Use an empty array to grant nothing, "
                    + "or list the tool keys this agent may invoke.");
                continue;
            }

            var blankTools = tools.Count(t => string.IsNullOrWhiteSpace(t));
            if (blankTools > 0)
            {
                errors.Add(
                    $"AllowedToolsByAgentId['{agentId}'] contains {blankTools} blank tool key, which "
                    + "can never match a registered tool.");
            }
        }
    }

    private void ValidateRegistrations(List<string> errors)
    {
        if (_services.GetService<IAgentIdentityValidator>() is null)
        {
            errors.Add(
                "No IAgentIdentityValidator is registered, so there is no policy oracle to consult "
                + "and every call would be refused. Call the Infrastructure AI dependency "
                + "registration, which registers it.");
        }

        if (_services.GetService<IAgentIdentityResolver>() is null)
        {
            errors.Add(
                "No IAgentIdentityResolver is registered, so an identity cannot be established on "
                + "the plan-engine and Execution API paths, whose DI scopes carry none. Call the "
                + "Infrastructure AI dependency registration, which registers it.");
        }
    }
}
