using Application.AI.Common.Evaluation.Interfaces;
using Application.AI.Common.Evaluation.Models;
using Application.AI.Common.Evaluation.Outcomes;
using Application.AI.Common.Interfaces;
using Domain.AI.KnowledgeGraph.Models;
using Domain.Common.Config;
using Domain.Common.Config.AI.HarmonicMemory;
using Infrastructure.AI.KnowledgeGraph.InMemory;
using Infrastructure.AI.KnowledgeGraph.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Presentation.EvalRunner.HarmonicWriteEval;

/// <summary>
/// Runs the harmonic memory write-side eval: for each mode (Off / AbstractOnly / Full) it remembers every
/// fixture fact through an isolated in-memory <see cref="KnowledgeMemoryService"/>, then measures
/// fragmentation, cluster purity, LLM-call cost, and (on a paid run) abstraction quality.
/// </summary>
/// <remarks>
/// Deliberately does NOT measure recall quality: pre-PR3 the recall path does not read abstractions, so a
/// recall eval would show no delta across modes. This eval targets the write-side hypothesis directly — does
/// consolidation cut fragmentation, at what cost, and are the abstractions good.
/// </remarks>
public static class HarmonicWriteEvalApp
{
    private static readonly HarmonicMemoryMode[] Modes =
        [HarmonicMemoryMode.Off, HarmonicMemoryMode.AbstractOnly, HarmonicMemoryMode.Full];

    /// <summary>Runs the eval across all modes and returns the scorecard.</summary>
    /// <param name="provider">The eval host service provider (for the chat-client factory and judge on paid runs).</param>
    /// <param name="fixture">The loaded fact fixture.</param>
    /// <param name="useLlm">When true, use the paid LLM providers + quality judge; otherwise the offline deterministic providers.</param>
    /// <param name="generatedAtUtc">ISO-8601 UTC timestamp for the report (supplied by the caller).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task<HarmonicWriteEvalReport> RunAsync(
        IServiceProvider provider,
        HarmonicWriteFixture fixture,
        bool useLlm,
        string generatedAtUtc,
        CancellationToken cancellationToken)
    {
        Func<EvalMemoryProviderSet> providerFactory;
        ILlmJudge? judge = null;

        if (useLlm)
        {
            var chatClientFactory = provider.GetRequiredService<IChatClientFactory>();
            var agentFramework = provider.GetRequiredService<IOptionsMonitor<AppConfig>>().CurrentValue.AI.AgentFramework;
            providerFactory = EvalMemoryProviderSet.Llm(
                chatClientFactory, agentFramework.ClientType, agentFramework.DefaultDeployment);
            judge = provider.GetRequiredService<ILlmJudge>();
        }
        else
        {
            providerFactory = EvalMemoryProviderSet.Deterministic();
        }

        var results = new List<HarmonicWriteModeResult>(Modes.Length);
        foreach (var mode in Modes)
            results.Add(await RunModeAsync(fixture, mode, providerFactory, judge, cancellationToken));

        return new HarmonicWriteEvalReport
        {
            FixtureDescription = fixture.Description,
            FactCount = fixture.Facts.Count,
            GoldTopicCount = fixture.GoldTopicCount,
            UsedLlm = useLlm,
            GeneratedAtUtc = generatedAtUtc,
            Modes = results
        };
    }

    private static async Task<HarmonicWriteModeResult> RunModeAsync(
        HarmonicWriteFixture fixture,
        HarmonicMemoryMode mode,
        Func<EvalMemoryProviderSet> providerFactory,
        ILlmJudge? judge,
        CancellationToken cancellationToken)
    {
        var graphStore = new InMemoryGraphStore(NullLogger<InMemoryGraphStore>.Instance);
        var appConfig = new AppConfig();
        appConfig.AI.HarmonicMemory.Mode = mode;

        // Off never touches the abstractor/consolidator, so leave them unwired (keeps call counts at 0).
        var providers = mode == HarmonicMemoryMode.Off ? null : providerFactory();

        var memory = new KnowledgeMemoryService(
            new InMemorySessionCache(),
            graphStore,
            new EvalKnowledgeScope(),
            feedbackDetector: null,
            feedbackStore: null,
            new StaticOptionsMonitor<AppConfig>(appConfig),
            NullLogger<KnowledgeMemoryService>.Instance,
            writeGate: null,
            abstractor: providers?.Abstractor,
            consolidator: providers?.Consolidator);

        foreach (var fact in fixture.Facts)
            await memory.RememberAsync(fact.Key, fact.Content, cancellationToken: cancellationToken);

        // Read the stored abstraction per fact — the memory node's Name is the fact's key.
        var nodes = await graphStore.GetAllNodesAsync(cancellationToken);
        var abstractionByKey = nodes
            .Where(n => n.GetAbstraction() is not null)
            .ToDictionary(n => n.Name, n => n.GetAbstraction()!, StringComparer.Ordinal);

        var assigned = fixture.Facts
            .Where(f => abstractionByKey.ContainsKey(f.Key))
            .Select(f => (Fact: f, Abstraction: abstractionByKey[f.Key]))
            .ToList();

        var factAbstractions = assigned
            .Select(x => new FactAbstraction { Abstraction = x.Abstraction, GoldTopic = x.Fact.GoldTopic })
            .ToList();

        // Fragmentation divides distinct abstractions (over facts that produced one) by every gold topic.
        // These stay consistent because the eval's AppConfig leaves MinContentLengthChars at 0, so every
        // fact takes the harmonic path and contributes an abstraction. If a length floor is ever introduced,
        // sub-threshold facts would drop from the numerator while their topics still count in the denominator.
        int? distinct = factAbstractions.Count > 0
            ? HarmonicWriteMetrics.DistinctAbstractions(factAbstractions)
            : null;
        double? fragmentation = distinct is { } d
            ? HarmonicWriteMetrics.FragmentationRatio(d, fixture.GoldTopicCount)
            : null;
        double? purity = factAbstractions.Count > 0
            ? HarmonicWriteMetrics.ClusterPurity(factAbstractions)
            : null;

        var (quality, judged) = judge is not null && assigned.Count > 0
            ? await JudgeQualityAsync(judge, assigned, cancellationToken)
            : (null, 0);

        return new HarmonicWriteModeResult
        {
            Mode = mode.ToString(),
            FactCount = fixture.Facts.Count,
            GoldTopicCount = fixture.GoldTopicCount,
            DistinctAbstractions = distinct,
            FragmentationRatio = fragmentation,
            ClusterPurity = purity,
            AbstractorCalls = providers?.AbstractorCalls() ?? 0,
            ConsolidatorCalls = providers?.ConsolidatorCalls() ?? 0,
            MeanAbstractionQuality = quality,
            AbstractionsJudged = judged
        };
    }

    private static async Task<(double? Mean, int Judged)> JudgeQualityAsync(
        ILlmJudge judge,
        IReadOnlyList<(HarmonicWriteFact Fact, string Abstraction)> assigned,
        CancellationToken cancellationToken)
    {
        double sum = 0;
        int judged = 0;
        foreach (var (fact, abstraction) in assigned)
        {
            var request = new LlmJudgeRequest
            {
                SystemPromptCore =
                    "Score from 0.0 to 1.0 how well LABEL is a canonical topic label for MEMORY: does it " +
                    "capture what the memory is fundamentally about, stay concise, and generalize so related " +
                    "memories would share it? Respond with JSON {\"score\": number, \"reasoning\": string}.",
                UserPromptTemplate = "MEMORY:\n{{memory}}\n\nLABEL:\n{{label}}",
                Variables = new Dictionary<string, string?> { ["memory"] = fact.Content, ["label"] = abstraction }
            };

            var result = await judge.JudgeAsync(request, cancellationToken);
            if (result.Outcome == LlmJudgeOutcome.Parsed)
            {
                sum += result.Score;
                judged++;
            }
        }

        return judged > 0 ? (sum / judged, judged) : (null, judged);
    }
}
