using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Application.AI.Common.Interfaces.Learnings;
using Application.AI.Common.Interfaces.RAG;
using Application.Core.CQRS.Learnings;
using Domain.AI.Governance;
using Domain.AI.RAG.Models;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.Learnings;
using Infrastructure.AI.KnowledgeGraph.Memory;
using Infrastructure.AI.KnowledgeGraph.Provenance;
using Infrastructure.AI.Learnings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Application.AI.Common.Tests.Fakes;

/// <summary>
/// Builds the real learnings write-and-recall pipeline for tests that need to drive it end to end:
/// the production write gate, the real <see cref="RememberCommandHandler"/>, and the real
/// <see cref="RecallQueryHandler"/> with its scoring stack.
/// </summary>
/// <remarks>
/// <para>
/// Two callers assemble this pipeline — the memory-poisoning regression tests and the ASI06 OWASP
/// fixture — and they had assembled it separately, down to identical embedding fakes. Issue #338
/// itself added a constructor parameter to <see cref="RememberCommandHandler"/>; the next such
/// change would have had to find both copies, and the eval fixture could have drifted until it
/// scored a pipeline the regression tests no longer covered.
/// </para>
/// <para>
/// What is deliberately <em>not</em> shared is the injection scanner. The fixture blanket-flags to
/// demonstrate the defence; the regression tests flag only the attack string so a legitimate lesson
/// written in the same run acts as a control. Those are different questions and each caller supplies
/// its own.
/// </para>
/// </remarks>
internal static class LearningsChannelHarness
{
    /// <summary>
    /// The production <see cref="ProvenanceMemoryWriteGate"/> on its default thresholds (quarantine
    /// at Medium, reject at Critical), with only the scanner — the one external boundary — supplied
    /// by the caller.
    /// </summary>
    /// <param name="scanner">Deterministic stand-in for the prompt-injection scanner.</param>
    /// <param name="options">Application configuration; see <see cref="DefaultOptions"/>.</param>
    internal static IMemoryWriteGate BuildWriteGate(
        IPromptInjectionScanner scanner,
        IOptionsMonitor<AppConfig> options) =>
        new ProvenanceMemoryWriteGate(
            new DefaultProvenanceStamper(options, TimeProvider.System),
            new NoOpMemoryIntentClassifier(),
            options,
            NullLogger<ProvenanceMemoryWriteGate>.Instance,
            scanner: scanner,
            audit: null);

    /// <summary>The real creation handler, wired to <paramref name="store"/> and <paramref name="gate"/>.</summary>
    internal static RememberCommandHandler BuildRememberHandler(
        ILearningsStore store,
        IMemoryWriteGate gate,
        IOptionsMonitor<AppConfig> options) =>
        new(store,
            Mock.Of<ILearningNotificationChannel>(),
            gate,
            options,
            TimeProvider.System,
            NullLogger<RememberCommandHandler>.Instance);

    /// <summary>
    /// The real recall handler — the point at which the write-time trust classification is enforced.
    /// </summary>
    internal static RecallQueryHandler BuildRecallHandler(
        ILearningsStore store,
        IOptionsMonitor<AppConfig> options) =>
        new(store,
            new DefaultLearningDecayService(
                store,
                OptionsMonitorOf(new LearningsConfig()),
                TimeProvider.System,
                NullLogger<DefaultLearningDecayService>.Instance),
            new ConstantEmbeddingService(),
            // Backs the fire-and-forget access-reinforcement write, which is not under test here.
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            options,
            TimeProvider.System,
            NullLogger<RecallQueryHandler>.Instance);

    /// <summary>
    /// Configuration with the memory guard at its defaults and the recall relevance floor at zero, so
    /// ranking can never be what withholds a poisoned lesson — only the trust filter can.
    /// </summary>
    internal static IOptionsMonitor<AppConfig> DefaultOptions() => OptionsMonitorOf(new AppConfig
    {
        AI = new AIConfig
        {
            KnowledgeBridge = new KnowledgeBridgeConfig(),
            Learnings = new LearningsConfig(),
            LearningsRecall = new LearningsRecallConfig { Enabled = true, MaxResults = 5, MinRelevance = 0 }
        }
    });

    internal static IOptionsMonitor<T> OptionsMonitorOf<T>(T value) where T : class =>
        Mock.Of<IOptionsMonitor<T>>(m => m.CurrentValue == value);

    /// <summary>
    /// Returns one fixed vector for every input, so every candidate scores an identical relevance.
    /// Relevance is then incapable of separating a poisoned lesson from a clean one.
    /// </summary>
    private sealed class ConstantEmbeddingService : IEmbeddingService
    {
        private static readonly ReadOnlyMemory<float> Vector = new([1f, 0f, 0f]);

        public Task<ReadOnlyMemory<float>> EmbedQueryAsync(string text, CancellationToken ct = default) =>
            Task.FromResult(Vector);

        public Task<IReadOnlyList<DocumentChunk>> EmbedAsync(
            IReadOnlyList<DocumentChunk> chunks, CancellationToken ct = default) =>
            throw new NotSupportedException("Learnings recall does not embed document chunks.");
    }
}
