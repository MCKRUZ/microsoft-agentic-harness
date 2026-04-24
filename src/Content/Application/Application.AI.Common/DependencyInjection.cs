using Application.AI.Common.Factories;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Context;
using Application.AI.Common.Interfaces.Tools;
using Application.AI.Common.MediatRBehaviors;
using Application.AI.Common.OpenTelemetry;
using Application.AI.Common.Services.Agent;
using Application.AI.Common.Services.Context;
using Application.AI.Common.Services.Tools;
using Application.Common.Interfaces.Telemetry;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace Application.AI.Common;

/// <summary>
/// Dependency injection configuration for the Application.AI.Common layer.
/// Registers agent-specific MediatR pipeline behaviors that depend on agentic
/// abstractions (agent context, tool permissions, content safety, audit).
/// </summary>
/// <remarks>
/// <para>
/// Called from the Presentation composition root after <c>AddApplicationCommonDependencies</c>:
/// <code>
/// services.AddApplicationCommonDependencies(appConfig);
/// services.AddApplicationAIDependencies();
/// </code>
/// </para>
/// <para>
/// <strong>Agent Pipeline Behavior Order:</strong>
/// These behaviors wrap the generic behaviors registered by Application.Common.
/// The combined pipeline (outermost → innermost):
/// <list type="number">
///   <item><description><c>UnhandledExceptionBehavior</c> — safety net with agent context enrichment</description></item>
///   <item><description><c>AgentContextPropagationBehavior</c> — sets scoped agent identity</description></item>
///   <item><description><c>AuditTrailBehavior</c> — records IAuditable requests</description></item>
///   <item><description><c>ContentSafetyBehavior</c> — screens IContentScreenable requests</description></item>
///   <item><description><c>ToolPermissionBehavior</c> — checks IToolRequest permissions</description></item>
///   <item><description><c>HookBehavior</c> — fires lifecycle hooks for tool and turn events</description></item>
/// </list>
/// </para>
/// </remarks>
public static class DependencyInjection
{
    /// <summary>
    /// Registers all Application.AI.Common dependencies into the service collection.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddApplicationAIDependencies(
        this IServiceCollection services)
    {
        // Agent-specific pipeline behaviors — registered before Application.Common
        // behaviors so they wrap as the outermost layer
        services
            .AddTransient(typeof(IPipelineBehavior<,>), typeof(UnhandledExceptionBehavior<,>))
            .AddTransient(typeof(IPipelineBehavior<,>), typeof(AgentContextPropagationBehavior<,>))
            .AddTransient(typeof(IPipelineBehavior<,>), typeof(AuditTrailBehavior<,>))
            .AddTransient(typeof(IPipelineBehavior<,>), typeof(ContentSafetyBehavior<,>))
            .AddTransient(typeof(IPipelineBehavior<,>), typeof(ToolPermissionBehavior<,>))
            .AddTransient(typeof(IPipelineBehavior<,>), typeof(HookBehavior<,>))
            .AddTransient(typeof(IPipelineBehavior<,>), typeof(RetrievalAuditBehavior<,>));

        // Scoped agent execution context — carries agent identity through the pipeline
        services.AddScoped<IAgentExecutionContext, AgentExecutionContext>();

        // AI telemetry configurator — registers AI SDK OTel sources and processors
        services.AddSingleton<ITelemetryConfigurator, AiTelemetryConfigurator>();

        // Agent factories — context mapping and agent creation
        services.AddSingleton<AgentExecutionContextFactory>();
        services.AddSingleton<IAgentFactory, AgentFactory>();

        // Tool conversion — ITool to AITool bridge for keyed DI tools
        services.AddSingleton<IToolConverter, AIToolConverter>();

        // Context budget tracking and tiered context assembly
        services.AddSingleton<IContextBudgetTracker, ContextBudgetTracker>();
        services.AddTransient<ITieredContextAssembler, TieredContextAssembler>();

        // LLM usage capture — scoped so middleware and handler share the same instance per turn
        services.AddScoped<ILlmUsageCapture, Services.LlmUsageCapture>();

        return services;
    }
}
