using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Microsoft.Extensions.Options;

namespace Presentation.EvalRunner.HarmonicWriteEval;

/// <summary>Fixed knowledge scope for the eval so every mode writes under one isolated namespace.</summary>
internal sealed class EvalKnowledgeScope : IKnowledgeScope
{
    public string? UserId => "harmonic-eval-user";
    public string? TenantId => "harmonic-eval-tenant";
    public string? DatasetId => null;
    public string? DatasetName => null;
    public string? DatasetOwnerId => null;
    public string? AgentId => null;
    public string? ConversationId => null;
}

/// <summary>Minimal <see cref="IOptionsMonitor{T}"/> serving one fixed value — enough to drive the mode per run.</summary>
internal sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue { get; } = value;
    public T Get(string? name) => CurrentValue;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}

/// <summary>A fresh abstractor + consolidator pair for one mode run, with live call-count accessors for the cost metric.</summary>
internal sealed class EvalMemoryProviderSet
{
    public required IMemoryAbstractor Abstractor { get; init; }
    public required IMemoryConsolidator Consolidator { get; init; }
    public required Func<int> AbstractorCalls { get; init; }
    public required Func<int> ConsolidatorCalls { get; init; }

    /// <summary>Builds a factory that mints a fresh deterministic (offline) provider set per call.</summary>
    public static Func<EvalMemoryProviderSet> Deterministic() => () =>
    {
        var abstractor = new DeterministicMemoryAbstractor();
        var consolidator = new DeterministicMemoryConsolidator();
        return new EvalMemoryProviderSet
        {
            Abstractor = abstractor,
            Consolidator = consolidator,
            AbstractorCalls = () => abstractor.Calls,
            ConsolidatorCalls = () => consolidator.Calls
        };
    };

    /// <summary>Builds a factory that mints a fresh LLM-backed (paid) provider set per call.</summary>
    public static Func<EvalMemoryProviderSet> Llm(
        IChatClientFactory chatClientFactory, AIAgentFrameworkClientType clientType, string deployment) => () =>
    {
        var abstractor = new LlmMemoryAbstractor(chatClientFactory, clientType, deployment);
        var consolidator = new LlmMemoryConsolidator(chatClientFactory, clientType, deployment);
        return new EvalMemoryProviderSet
        {
            Abstractor = abstractor,
            Consolidator = consolidator,
            AbstractorCalls = () => abstractor.Calls,
            ConsolidatorCalls = () => consolidator.Calls
        };
    };
}
