using Application.AI.Common.Interfaces.Permissions;
using Domain.AI.Permissions;
using Domain.Common.Config;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Permissions;

/// <summary>
/// Loads permission rules from the application configuration (<c>AppConfig:AI:Permissions</c>).
/// Currently returns an empty rule set; rules are added via appsettings.json configuration.
/// </summary>
/// <remarks>
/// This provider serves as the baseline config-driven rule source. Additional providers
/// (manifest-based, session-based) can be registered alongside it via DI.
/// </remarks>
public sealed class ConfigBasedRuleProvider : IPermissionRuleProvider
{
    private readonly IOptionsMonitor<AppConfig> _appConfig;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConfigBasedRuleProvider"/> class.
    /// </summary>
    /// <param name="appConfig">Application configuration containing permission rules.</param>
    public ConfigBasedRuleProvider(IOptionsMonitor<AppConfig> appConfig)
    {
        _appConfig = appConfig;
    }

    /// <inheritdoc />
    public PermissionRuleSource Source => PermissionRuleSource.ProjectSettings;

    /// <inheritdoc />
    public Task<IReadOnlyList<ToolPermissionRule>> GetRulesAsync(
        string agentId,
        CancellationToken cancellationToken = default)
    {
        // Config-based rules are loaded from AppConfig.AI.Permissions.
        // Currently the config model stores SafetyGatePaths and DefaultBehavior
        // but not explicit rule objects. This provider returns an empty list,
        // ready for extension when PermissionsConfig gains a Rules collection.
        IReadOnlyList<ToolPermissionRule> rules = [];
        return Task.FromResult(rules);
    }
}
