using Application.AI.Common.Evaluation.Interfaces;
using Application.AI.Common.Evaluation.Models;
using Infrastructure.AI.Evaluation.Invokers;
using Infrastructure.AI.Evaluation.Judges;
using Infrastructure.AI.Evaluation.Loaders;
using Infrastructure.AI.Evaluation.Metrics;
using Infrastructure.AI.Evaluation.Metrics.Rag;
using Infrastructure.AI.Evaluation.Reporters;
using Infrastructure.AI.Evaluation.Runners;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure.AI.Evaluation;

/// <summary>
/// DI registrations for the offline evaluation framework.
/// </summary>
/// <remarks>
/// Wires the loader, runner, invoker, all built-in metrics, and all built-in reporters.
/// Consumers can override or extend any of these by registering a different
/// <see cref="IEvalMetric"/>, <see cref="IEvalReporter"/>, or <see cref="IEvalDatasetLoader"/>
/// implementation after calling <see cref="AddEvaluationDependencies"/>.
/// </remarks>
public static class DependencyInjection
{
    /// <summary>
    /// Registers the eval framework: dataset loaders, metrics, reporters,
    /// runner, MediatR-backed agent invoker, and the <see cref="JudgeCostOptions"/>
    /// option block (defaults to zero rates).
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configureJudgeCost">
    /// Optional callback to set <see cref="JudgeCostOptions"/> rates. When omitted,
    /// cost reporting stays at <c>$0</c> until the consumer wires
    /// <c>services.Configure&lt;JudgeCostOptions&gt;(...)</c> manually.
    /// </param>
    /// <returns>The service collection, for chaining.</returns>
    public static IServiceCollection AddEvaluationDependencies(
        this IServiceCollection services,
        Action<JudgeCostOptions>? configureJudgeCost = null)
    {
        // Loaders
        services.AddSingleton<IEvalDatasetLoader, YamlEvalDatasetLoader>();

        // Metrics — register both as the concrete IEvalMetric set and as keyed services
        // so future EvalRunner / handler can resolve a specific metric by Key without
        // walking the whole enumeration.
        AddMetric<ExactMatchMetric>(services, "exact_match");
        AddMetric<RegexMatchMetric>(services, "regex_match");
        AddMetric<ContainsAllMetric>(services, "contains_all");
        AddMetric<DoesNotContainMetric>(services, "does_not_contain");
        AddMetric<IsValidJsonMetric>(services, "is_valid_json");
        AddMetric<LlmJudgeMetric>(services, "llm_judge");

        // Routing-accuracy: compares a router's predicted label to the case's gold label.
        // Paired with RouterEvalInvoker (below) and the router probes registered by the RAG
        // and routing infrastructure projects.
        AddMetric<RoutingAccuracyMetric>(services, "routing_accuracy");

        // RAG metric pack: faithfulness, context precision/recall, answer relevance/correctness.
        // All share ILlmJudge + IPromptRegistry (registry registered separately via
        // AddPromptRegistry — the eval framework consumes it, does not own it).
        AddMetric<FaithfulnessMetric>(services, "faithfulness");
        AddMetric<ContextPrecisionMetric>(services, "context_precision");
        AddMetric<ContextRecallMetric>(services, "context_recall");
        AddMetric<AnswerRelevanceMetric>(services, "answer_relevance");
        AddMetric<AnswerCorrectnessMetric>(services, "answer_correctness");

        // Reporters
        services.AddSingleton<IEvalReporter, JsonEvalReporter>();
        services.AddSingleton<IEvalReporter, JUnitXmlEvalReporter>();
        services.AddSingleton<IEvalReporter, ConsoleEvalReporter>();

        // Runner + invoker. RouterEvalInvoker decorates the harness invoker: cases that opt in via
        // a `target: "router:<key>"` override are dispatched to a router probe (resolved from the
        // probes registered by the RAG / routing infrastructure projects), while every other case
        // passes through to HarnessAgentInvoker unchanged. When no probes are registered (e.g. an
        // eval host that composes only this project), router-target cases fail soft and agent cases
        // are unaffected.
        services.AddSingleton<IEvalRunner, EvalRunner>();
        services.AddSingleton<HarnessAgentInvoker>();
        services.AddSingleton<IAgentInvoker, RouterEvalInvoker>();

        // Fixed judge chat client (NOT model-router) — preserves cross-run reproducibility.
        services.AddSingleton<IJudgeChatClientProvider, DefaultJudgeChatClientProvider>();
        services.AddOptions<JudgeOptions>();

        // Shared judge call mechanics used by LlmJudgeMetric and the RAG metric pack.
        services.AddSingleton<ILlmJudge, DefaultLlmJudge>();

        // NOTE: RAG metrics resolve their prompts via IPromptRegistry; the registry is
        // wired by the composition root through AddPromptRegistry(...) so the eval
        // framework stays decoupled from the prompts root path.

        // Cost rates — defaults to $0; consumers configure via the optional callback
        // or by registering their own Configure<JudgeCostOptions>(...) after this call.
        var optionsBuilder = services.AddOptions<JudgeCostOptions>();
        if (configureJudgeCost is not null)
        {
            optionsBuilder.Configure(configureJudgeCost);
        }

        return services;
    }

    private static void AddMetric<TMetric>(IServiceCollection services, string key)
        where TMetric : class, IEvalMetric
    {
        services.AddSingleton<TMetric>();
        services.AddSingleton<IEvalMetric>(sp => sp.GetRequiredService<TMetric>());
        services.AddKeyedSingleton<IEvalMetric>(key, (sp, _) => sp.GetRequiredService<TMetric>());
    }
}
