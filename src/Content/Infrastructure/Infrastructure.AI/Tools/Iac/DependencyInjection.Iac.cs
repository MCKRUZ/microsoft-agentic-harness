using Application.AI.Common.Interfaces.Iac;
using Application.AI.Common.Interfaces.Sandbox;
using Application.AI.Common.Interfaces.Tools;
using Domain.AI.Iac;
using Domain.AI.Sandbox;
using Domain.Common.Config;
using Infrastructure.AI.Iac;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Tools.Iac;

/// <summary>
/// DI registration for the IaC skill pack (PR-10): the Terraform and Bicep
/// <see cref="IIacGenerator"/> implementations keyed by backend, the three
/// agent-facing tools keyed by tool name, and the fail-loud startup validator.
/// Composition-root opt-in — callers invoke <see cref="AddIacSkillTools"/> from
/// their <c>Add*Dependencies</c> chain so the skill pack is bolted on only when
/// wanted.
/// </summary>
/// <remarks>
/// <para>
/// Registers, by keyed-DI name:
/// <list type="bullet">
///   <item><c>terraform</c> / <c>bicep</c> — <see cref="IIacGenerator"/> implementations</item>
///   <item><c>iac_generate</c> — <see cref="IacGenerateTool"/></item>
///   <item><c>iac_plan</c>     — <see cref="IacPlanTool"/></item>
///   <item><c>iac_scan</c>     — <see cref="IacScanTool"/></item>
/// </list>
/// </para>
/// <para>
/// The generators are keyed SINGLETONS but <see cref="ISandboxExecutor"/> is keyed
/// SCOPED, so each generator resolves the <c>Process</c>-isolation executor per CLI
/// run from a fresh DI scope (via <see cref="IServiceScopeFactory"/>) — the same
/// pattern the workspace skill pack uses for its verifier tools. A construction-time
/// executor would be a captive dependency rejected by scope validation. Registrations
/// are unconditional (the tools are inert until a skill resolves them);
/// <see cref="IacStartupValidator"/> turns a bad config into a fail-loud boot error
/// whenever the skill pack is enabled.
/// </para>
/// </remarks>
public static class IacDependencyInjection
{
    /// <summary>
    /// Registers the IaC skill pack's generators, agent-facing tools, and startup
    /// validator on the supplied <see cref="IServiceCollection"/>.
    /// </summary>
    /// <param name="services">The service collection to register into.</param>
    /// <returns>The same collection, for fluent chaining.</returns>
    public static IServiceCollection AddIacSkillTools(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // --- Generators, keyed by backend canonical key ---
        // The generators take the scope factory (not ISandboxExecutor): the executor is
        // keyed SCOPED, so each CLI run resolves it from a fresh scope instead of the
        // singleton factory capturing it from the root provider.
        services.AddKeyedSingleton<IIacGenerator>(IacBackendKeys.Terraform, static (sp, _) =>
            new TerraformGenerator(
                sp.GetRequiredService<IOptionsMonitor<AppConfig>>(),
                sp.GetRequiredService<IServiceScopeFactory>(),
                sp.GetRequiredService<ILogger<TerraformGenerator>>(),
                sp.GetRequiredService<TimeProvider>(),
                SandboxIsolationLevel.Process));

        services.AddKeyedSingleton<IIacGenerator>(IacBackendKeys.Bicep, static (sp, _) =>
            new BicepGenerator(
                sp.GetRequiredService<IOptionsMonitor<AppConfig>>(),
                sp.GetRequiredService<IServiceScopeFactory>(),
                sp.GetRequiredService<ILogger<BicepGenerator>>(),
                sp.GetRequiredService<TimeProvider>(),
                SandboxIsolationLevel.Process));

        // --- Agent-facing tools (keyed by ToolName) ---
        services.AddKeyedSingleton<ITool>(IacGenerateTool.ToolName, static (sp, _) =>
            new IacGenerateTool(sp, sp.GetRequiredService<IOptionsMonitor<AppConfig>>()));

        services.AddKeyedSingleton<ITool>(IacPlanTool.ToolName, static (sp, _) =>
            new IacPlanTool(sp, sp.GetRequiredService<IOptionsMonitor<AppConfig>>()));

        services.AddKeyedSingleton<ITool>(IacScanTool.ToolName, static (sp, _) =>
            new IacScanTool(sp, sp.GetRequiredService<IOptionsMonitor<AppConfig>>()));

        // --- Fail-loud startup validation when the skill pack is enabled ---
        services.AddHostedService<IacStartupValidator>();

        return services;
    }
}
