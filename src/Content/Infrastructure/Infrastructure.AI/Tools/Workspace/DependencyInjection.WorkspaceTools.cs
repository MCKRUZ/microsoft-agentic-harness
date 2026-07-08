using Application.AI.Common.Interfaces.Sandbox;
using Application.AI.Common.Interfaces.Tools;
using Application.AI.Common.Interfaces.Workspace;
using Domain.AI.Sandbox;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure.AI.Tools.Workspace;

/// <summary>
/// DI registration for the workspace skill pack's tools and ambient
/// <see cref="IWorkspaceContextAccessor"/>. Composition root opt-in: callers
/// invoke <see cref="AddWorkspaceSkillTools"/> from their
/// <c>Add*Dependencies</c> chain so the workspace skill pack is bolted on
/// only when the consumer wants it.
/// </summary>
/// <remarks>
/// <para>
/// Registers, by keyed-DI name:
/// <list type="bullet">
///   <item><c>read_file</c>  — <see cref="WorkspaceReadFileTool"/></item>
///   <item><c>write_file</c> — <see cref="WorkspaceWriteFileTool"/></item>
///   <item><c>list_files</c> — <see cref="WorkspaceListFilesTool"/></item>
///   <item><c>run_tests</c>  — <see cref="WorkspaceRunTestsTool"/></item>
///   <item><c>run_lint</c>   — <see cref="WorkspaceRunLintTool"/></item>
/// </list>
/// </para>
/// <para>
/// The accessor is a singleton because the backing store is per-async-flow,
/// not per-DI-scope. Tools are singletons; scope-bound collaborators (the
/// keyed-SCOPED <see cref="ISandboxExecutor"/>, the request-scoped MediatR
/// pipeline) are resolved per execution from a fresh scope via
/// <see cref="IServiceScopeFactory"/> — never captured at construction, so a
/// singleton tool can never hold captive scoped state.
/// </para>
/// <para>
/// The sandbox executor is registered keyed SCOPED on
/// <see cref="SandboxIsolationLevel"/> elsewhere in the DI graph. The
/// workspace tools pull the <c>Process</c> isolation level by default — the
/// sandbox lives on the host but enforces capability + resource limits via
/// Job Objects (Windows) / cgroups (Linux). Consumers that prefer Docker can
/// override the registration after calling this method (the tools accept the
/// isolation level as a constructor argument).
/// </para>
/// </remarks>
public static class WorkspaceDependencyInjection
{
    /// <summary>
    /// Registers the workspace skill pack's tools, ambient context accessor,
    /// and default sandbox-isolation binding on the supplied
    /// <see cref="IServiceCollection"/>.
    /// </summary>
    /// <param name="services">The service collection to register into.</param>
    /// <returns>The same collection, for fluent chaining.</returns>
    public static IServiceCollection AddWorkspaceSkillTools(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IWorkspaceContextAccessor, WorkspaceContextAccessor>();

        services.AddKeyedSingleton<ITool>(WorkspaceReadFileTool.ToolName, (sp, _) =>
            new WorkspaceReadFileTool(sp.GetRequiredService<IWorkspaceContextAccessor>()));

        services.AddKeyedSingleton<ITool>(WorkspaceListFilesTool.ToolName, (sp, _) =>
            new WorkspaceListFilesTool(sp.GetRequiredService<IWorkspaceContextAccessor>()));

        services.AddKeyedSingleton<ITool>(WorkspaceWriteFileTool.ToolName, (sp, _) =>
            new WorkspaceWriteFileTool(
                sp.GetRequiredService<IWorkspaceContextAccessor>(),
                sp.GetRequiredService<IServiceScopeFactory>()));

        services.AddKeyedSingleton<ITool>(WorkspaceRunTestsTool.ToolName, (sp, _) =>
            new WorkspaceRunTestsTool(
                sp.GetRequiredService<IWorkspaceContextAccessor>(),
                sp.GetRequiredService<IServiceScopeFactory>(),
                SandboxIsolationLevel.Process));

        services.AddKeyedSingleton<ITool>(WorkspaceRunLintTool.ToolName, (sp, _) =>
            new WorkspaceRunLintTool(
                sp.GetRequiredService<IWorkspaceContextAccessor>(),
                sp.GetRequiredService<IServiceScopeFactory>(),
                SandboxIsolationLevel.Process));

        return services;
    }
}
