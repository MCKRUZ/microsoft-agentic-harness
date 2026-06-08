using Application.AI.Common.Interfaces.Attestation;
using Application.AI.Common.Interfaces.Planner;
using Application.AI.Common.Interfaces.Sandbox;
using Domain.AI.Planner;
using Domain.AI.Sandbox;
using Domain.Common.Config;
using Infrastructure.AI.Attestation;
using Infrastructure.AI.Persistence;
using Infrastructure.AI.Planner;
using Infrastructure.AI.Planner.StepExecutors;
using Infrastructure.AI.Sandbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure.AI;

public static partial class DependencyInjection
{
    /// <summary>
    /// Registers the SQLite-backed <see cref="PlannerDbContext"/> with a connection string
    /// derived from the configured database path. Creates the data directory if absent.
    /// </summary>
    private static void RegisterPlannerDbContext(IServiceCollection services, AppConfig appConfig)
    {
        var dbPath = appConfig.AI.Planner.DatabasePath;
        var dataDir = Path.GetDirectoryName(Path.Combine(AppContext.BaseDirectory, dbPath))!;
        Directory.CreateDirectory(dataDir);
        var connectionString = $"DataSource={Path.Combine(AppContext.BaseDirectory, dbPath)}";

        services.AddDbContextFactory<PlannerDbContext>(options => options.UseSqlite(connectionString));
        services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<PlannerDbContext>>().CreateDbContext());
    }

    /// <summary>
    /// Registers planner services: executor, validator, generator, state store, execution context,
    /// and keyed step executors for each <see cref="StepType"/>.
    /// </summary>
    private static void RegisterPlannerServices(IServiceCollection services)
    {
        services.AddScoped<IPlanExecutor, PlanExecutor>();
        services.AddScoped<IPlanValidator, PlanValidator>();
        services.AddScoped<IPlanGenerator, LlmPlanGeneratorService>();
        services.AddScoped<IPlanStateStore, EfCorePlanStateStore>();
        services.AddScoped<PlanExecutionContext>();

        services.AddKeyedScoped<IPlanStepExecutor>(StepType.LlmCall,
            (sp, _) => sp.GetRequiredService<LlmCallStepExecutor>());
        services.AddKeyedScoped<IPlanStepExecutor>(StepType.ToolUse,
            (sp, _) => sp.GetRequiredService<ToolUseStepExecutor>());
        services.AddKeyedScoped<IPlanStepExecutor>(StepType.HumanGate,
            (sp, _) => sp.GetRequiredService<HumanGateStepExecutor>());
        services.AddKeyedScoped<IPlanStepExecutor>(StepType.ConditionalBranch,
            (sp, _) => sp.GetRequiredService<ConditionalBranchStepExecutor>());
        services.AddKeyedScoped<IPlanStepExecutor>(StepType.SubPlanInvocation,
            (sp, _) => sp.GetRequiredService<SubPlanStepExecutor>());
        services.AddKeyedScoped<IPlanStepExecutor>(StepType.Retrieval,
            (sp, _) => sp.GetRequiredService<RetrievalPlanStepExecutor>());

        services.AddScoped<LlmCallStepExecutor>();
        services.AddScoped<ToolUseStepExecutor>();
        services.AddScoped<HumanGateStepExecutor>();
        services.AddScoped<ConditionalBranchStepExecutor>();
        services.AddScoped<SubPlanStepExecutor>();
        services.AddScoped<RetrievalPlanStepExecutor>();
    }

    /// <summary>
    /// Registers sandbox execution services: process and container executors (keyed by
    /// <see cref="SandboxIsolationLevel"/>), Docker client, attestation, and platform-specific
    /// resource limiters.
    /// </summary>
    private static void RegisterSandboxServices(IServiceCollection services)
    {
        services.AddKeyedScoped<ISandboxExecutor>(SandboxIsolationLevel.Process,
            (sp, _) => sp.GetRequiredService<ProcessSandboxExecutor>());
        services.AddKeyedScoped<ISandboxExecutor>(SandboxIsolationLevel.Container,
            (sp, _) => sp.GetRequiredService<DockerSandboxExecutor>());

        services.AddScoped<ProcessSandboxExecutor>();
        services.AddScoped<DockerSandboxExecutor>();

        // PR-3c: sandbox-side egress preflight gate. Optional injection on the
        // executors; the executor falls back to legacy attestation when no
        // preflight is registered. Activated unconditionally here so the
        // sandbox cannot bypass policy by default in any composed host.
        services.AddScoped<Application.AI.Common.Interfaces.Sandbox.ISandboxEgressPreflight, Infrastructure.AI.Sandbox.SandboxEgressPreflight>();

        services.AddSingleton<Docker.DotNet.IDockerClient>(_ =>
            new Docker.DotNet.DockerClientConfiguration().CreateClient());

        services.AddScoped<IAttestationService, HmacAttestationService>();

        if (OperatingSystem.IsWindows())
            services.AddSingleton<IProcessResourceLimiter, WindowsProcessResourceLimiter>();
        else
            services.AddSingleton<IProcessResourceLimiter, NoOpProcessResourceLimiter>();
    }
}
