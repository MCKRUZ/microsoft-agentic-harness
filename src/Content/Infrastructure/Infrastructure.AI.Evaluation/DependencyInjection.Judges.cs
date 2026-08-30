using Application.AI.Common.Evaluation.Interfaces;
using Application.AI.Common.Evaluation.Models;
using Infrastructure.AI.Evaluation.Judges;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure.AI.Evaluation;

/// <summary>
/// <see cref="ILlmJudge"/> DI registration, factored out of <see cref="DependencyInjection.AddEvaluationDependencies(IServiceCollection, Action{JudgeCostOptions})"/>
/// so a consumer that needs an LLM judge WITHOUT the rest of the eval framework (dataset loaders,
/// eval metrics, reporters, <c>IEvalRunner</c>) — e.g. obligation-based analysis (#320) — can wire it
/// alone. Mirrors <c>AddPromptRegistry</c>'s factoring-out shape (<c>Infrastructure.AI/Prompts/DependencyInjection.Prompts.cs</c>):
/// a small, independently-callable extension method rather than a private helper only the larger
/// registration can reach.
/// </summary>
public static class DependencyInjectionJudges
{
    /// <summary>
    /// Registers <see cref="ILlmJudge"/> and its collaborators: a fixed (non-model-router) judge chat
    /// client for cross-run reproducibility, <see cref="JudgeOptions"/>, and the judge panel
    /// ("jury") — <see cref="ILlmJudge"/> resolves to <see cref="JuryLlmJudge"/>, which delegates to
    /// the single <see cref="DefaultLlmJudge"/> when no panel is configured (the default,
    /// byte-identical single-judge cost/behavior) and only runs a panel when a consumer populates
    /// <see cref="JuryOptions.Panelists"/>.
    /// </summary>
    /// <remarks>
    /// Idempotent to call alongside <see cref="DependencyInjection.AddEvaluationDependencies(IServiceCollection, Action{JudgeCostOptions})"/>: both register the same
    /// six services, and .NET's DI container resolves repeated identical registrations to the last
    /// one added without error. A consumer wiring both is not a misconfiguration.
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection, for chaining.</returns>
    public static IServiceCollection AddLlmJudge(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IJudgeChatClientProvider, DefaultJudgeChatClientProvider>();
        services.AddOptions<JudgeOptions>();
        services.AddOptions<JuryOptions>();
        services.AddSingleton<DefaultLlmJudge>();
        services.AddSingleton<JuryLlmJudge>();
        services.AddSingleton<ILlmJudge>(sp => sp.GetRequiredService<JuryLlmJudge>());

        return services;
    }
}
