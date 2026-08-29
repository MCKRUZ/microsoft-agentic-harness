using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Context;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Application.AI.Common.Interfaces.Learnings;
using Application.AI.Common.Services.Agent;
using Application.AI.Common.Services.Tools;
using Domain.AI.KnowledgeGraph.Models;
using Domain.AI.Learnings;
using Domain.AI.Planner;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using FluentAssertions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.Services.Agent;

/// <summary>
/// The merge-contract suite: every <see cref="AIContextProvider"/> in this assembly, driven through the
/// public <see cref="AIContextProvider.InvokingAsync"/> entry point the runtime actually uses.
/// </summary>
/// <remarks>
/// <para>
/// <c>ProvideAIContextAsync</c> is contractually <em>additive</em>. The base merge computes
/// <c>Tools = input.Concat(provided)</c> and <c>Instructions = input + "\n" + provided</c>, so a provider
/// that answers there with a filtered list, a wrapped list, or the input instructions echoed back has its
/// removals undone and its contributions duplicated before the model sees anything.
/// </para>
/// <para>
/// Four providers in this assembly made that mistake. None of their unit tests caught it, because every
/// one of them called the <em>protected</em> hook (or an internal helper) directly and so never exercised
/// the merge the runtime always applies. This suite exists so that class of defect cannot recur silently:
/// it enumerates the subclasses by reflection and fails if any one of them is missing a factory here.
/// </para>
/// <para>
/// Each provider is deliberately constructed in its <em>active</em> configuration — recall enabled and
/// backed by a store that returns results, tools that are real <see cref="AIFunction"/> instances so
/// governance wrapping actually triggers. A provider that no-ops would pass every assertion below while
/// proving nothing.
/// </para>
/// </remarks>
public sealed class AIContextProviderMergeContractTests
{
    private const string SystemSentinel = "SYSTEM-PROMPT-SENTINEL";
    private const string UserQuery = "what did we learn?";

    /// <summary>
    /// Real functions, not mocks: <see cref="GoverningToolContextProvider"/> only wraps
    /// <see cref="AIFunction"/> instances, so a mocked <see cref="AITool"/> would skip the wrapping path
    /// entirely and let the duplication defect pass unnoticed.
    /// </summary>
    private static AITool MakeFunctionTool(string name) => AIFunctionFactory.Create(
        () => "ok", new AIFunctionFactoryOptions { Name = name, Description = "t" });

    private static AIContextProvider.InvokingContext MakeContext(AIContext aiContext) =>
        new(new Mock<AIAgent>().Object, new Mock<AgentSession>().Object, aiContext);

    private static AIContext ActiveInput() => new()
    {
        Instructions = SystemSentinel,
        Messages = new List<ChatMessage> { new(ChatRole.User, UserQuery) },
        Tools = [MakeFunctionTool("alpha"), MakeFunctionTool("beta")]
    };

    // ── one factory per subclass, each in its ACTIVE configuration ───────────

    /// <summary>
    /// Every <see cref="AIContextProvider"/> this suite covers, and how to build each one in its active
    /// configuration. This is the single source of truth: the theory data, the factory lookup, and the
    /// exhaustiveness guard all read from it, so registering a new provider is one edit rather than
    /// three that the compiler cannot keep in step.
    /// </summary>
    private static readonly Dictionary<string, Func<AIContextProvider>> Factories = new()
    {
        [nameof(ToolPermissionFilter)] = () => new ToolPermissionFilter(["alpha", "beta"]),
        [nameof(GoverningToolContextProvider)] = () =>
            new GoverningToolContextProvider(
                NullLogger<GoverningToolContextProvider>.Instance,
                Application.AI.Common.Tests.Governance.AdmissionHarness.PermissiveSanitizer()),
        [nameof(KnowledgeMemoryContextProvider)] = BuildKnowledgeMemory,
        [nameof(LearningsRecallContextProvider)] = BuildLearningsRecall,
        [nameof(PeerAgentContextProvider)] = BuildPeerAgentContext,
        [nameof(PerTurnBudgetContextProvider)] = () => new PerTurnBudgetContextProvider(
            "MergeContractAgent",
            new Mock<IContextBudgetTracker>().Object,
            SystemSentinel,
            baselineToolCount: 2,
            NullLogger<PerTurnBudgetContextProvider>.Instance),
    };

    public static TheoryData<string> AllProviders
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var name in Factories.Keys)
                data.Add(name);
            return data;
        }
    }

    private static AIContextProvider Create(string providerName) => Factories[providerName]();

    private static KnowledgeMemoryContextProvider BuildKnowledgeMemory()
    {
        var memory = new Mock<IKnowledgeMemory>();
        memory.Setup(m => m.RecallAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new GraphNode
                {
                    Id = "memory:1",
                    Name = "fact-key",
                    Type = "Fact",
                    Properties = new Dictionary<string, string> { ["content"] = "The user prefers dark mode." }
                }
            });

        var services = new ServiceCollection();
        services.AddSingleton(memory.Object);
        var scope = services.BuildServiceProvider();

        var config = new AppConfig
        {
            AI = new AIConfig { KnowledgeBridge = new KnowledgeBridgeConfig { Enabled = true } }
        };

        return new KnowledgeMemoryContextProvider(
            Mock.Of<IAmbientRequestScope>(a => a.Current == (IServiceProvider)scope),
            Mock.Of<IOptionsMonitor<AppConfig>>(m => m.CurrentValue == config),
            NullLogger<KnowledgeMemoryContextProvider>.Instance);
    }

    private static LearningsRecallContextProvider BuildLearningsRecall()
    {
        var lesson = new WeightedLearning
        {
            Learning = new LearningEntry
            {
                LearningId = Guid.NewGuid(),
                Category = LearningCategory.ToolUsagePattern,
                DecayClass = DecayClass.Stable,
                Scope = new LearningScope { IsGlobal = true },
                Content = "Prefer batching related fixes.",
                Source = new LearningSource
                {
                    SourceType = LearningSourceType.AgentSelfImprovement,
                    SourceId = "run-1",
                    SourceDescription = "synthesis"
                },
                Provenance = new LearningProvenance
                {
                    OriginPipeline = "work_memory_synthesis",
                    OriginTask = "overnight_synthesis",
                    OriginTimestamp = DateTimeOffset.UtcNow,
                    Confidence = 0.9
                },
                CreatedAt = DateTimeOffset.UtcNow
            },
            RelevanceScore = 0.8,
            FeedbackScore = 1.0,
            FreshnessScore = 1.0,
            FinalScore = 0.85
        };

        var recaller = new Mock<ILearningRecaller>();
        recaller.Setup(r => r.RecallAsync(
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { lesson });

        var services = new ServiceCollection();
        services.AddSingleton(recaller.Object);
        var scope = services.BuildServiceProvider();

        var config = new AppConfig
        {
            AI = new AIConfig { LearningsRecall = new LearningsRecallConfig { Enabled = true } }
        };

        return new LearningsRecallContextProvider(
            Mock.Of<IAmbientRequestScope>(a => a.Current == (IServiceProvider)scope),
            Mock.Of<IOptionsMonitor<AppConfig>>(m => m.CurrentValue == config),
            NullLogger<LearningsRecallContextProvider>.Instance);
    }

    private static PeerAgentContextProvider BuildPeerAgentContext()
    {
        var registry = new Mock<Application.AI.Common.Interfaces.IAgentMetadataRegistry>();
        registry.Setup(r => r.GetAll()).Returns(new List<Domain.AI.Agents.AgentDefinition>
        {
            new()
            {
                Id = "peer-agent",
                Name = "Peer Agent",
                Description = "Handles peer-shaped work."
            },
            new()
            {
                Id = "self-agent",
                Name = "Self Agent",
                Description = "Must not appear — this is the owning agent's own id."
            }
        });

        // owningAgentId "self-agent" proves self-exclusion is exercised, not just the happy path of
        // an agent with no peers at all — the active configuration for this provider is "at least one
        // OTHER peer visible, and the caller's own entry filtered out."
        return new PeerAgentContextProvider(registry.Object, owningAgentId: "self-agent");
    }

    // ── the guard: no subclass may escape this suite ─────────────────────────

    [Fact]
    public void EveryAIContextProviderSubclass_HasAFactoryInThisSuite()
    {
        // Reflection rather than a hand-maintained list: a fifth provider added later must be covered
        // here or this test fails, which is the only reason the defect stopped at four.
        var declared = typeof(ToolPermissionFilter).Assembly
            .GetTypes()
            .Where(t => t is { IsAbstract: false, IsClass: true } && typeof(AIContextProvider).IsAssignableFrom(t))
            .Select(t => t.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        var covered = Factories.Keys.OrderBy(n => n, StringComparer.Ordinal).ToList();

        declared.Should().BeEquivalentTo(covered,
            "every AIContextProvider must be driven through InvokingAsync by this suite");
    }

    // ── the contract ─────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(AllProviders))]
    public async Task Provider_DoesNotDuplicateInputTools(string providerName)
    {
        var result = await Create(providerName).InvokingAsync(MakeContext(ActiveInput()));

        var names = result.Tools?.Select(t => t.Name).ToList() ?? [];
        names.Should().OnlyHaveUniqueItems(
            "the base merge concatenates a provider's tools onto the input, so echoing or re-wrapping " +
            "the input tools publishes each one twice");
    }

    [Theory]
    [MemberData(nameof(AllProviders))]
    public async Task Provider_EmitsInputInstructionsExactlyOnce(string providerName)
    {
        var result = await Create(providerName).InvokingAsync(MakeContext(ActiveInput()));

        // Exactly one, which pins both failure directions at once: more than one means the provider
        // echoed the input that the base merge already prepends, sending the whole system prompt to
        // the model twice; fewer than one means it dropped the prompt contributed ahead of it.
        CountOccurrences(result.Instructions, SystemSentinel).Should().Be(1);
    }

    // ── provider-specific behaviour that only the merge reveals ──────────────

    [Fact]
    public async Task KnowledgeMemory_StillInjectsItsRecalledBlockExactlyOnce()
    {
        // Guards against a vacuous pass: the provider must actually be doing its job above.
        var result = await Create(nameof(KnowledgeMemoryContextProvider))
            .InvokingAsync(MakeContext(ActiveInput()));

        CountOccurrences(result.Instructions, "Relevant remembered context").Should().Be(1);
        result.Instructions.Should().Contain("The user prefers dark mode.");
    }

    [Fact]
    public async Task LearningsRecall_StillInjectsItsRecalledBlockExactlyOnce()
    {
        var result = await Create(nameof(LearningsRecallContextProvider))
            .InvokingAsync(MakeContext(ActiveInput()));

        CountOccurrences(result.Instructions, "Lessons from past work").Should().Be(1);
        result.Instructions.Should().Contain("Prefer batching related fixes.");
    }

    [Fact]
    public async Task PeerAgentContext_StillInjectsThePeerBlockExactlyOnceAndExcludesSelf()
    {
        var result = await Create(nameof(PeerAgentContextProvider))
            .InvokingAsync(MakeContext(ActiveInput()));

        CountOccurrences(result.Instructions, "peer-agent").Should().Be(1);
        result.Instructions.Should().Contain("Handles peer-shaped work.");
        result.Instructions.Should().NotContain("self-agent",
            "the owning agent's own id must never appear as one of its own delegation targets");
    }

    [Fact]
    public async Task PeerAgentContext_NoOwningAgentId_InjectsNothing()
    {
        // #518 correctness-review regression: a caller with no owning agent id (an orchestrator built
        // with no OwningAgentId, or the parameterless CreateAgentFromSkillAsync(skillId) overload) used
        // to have "nothing excluded" read as "inject the whole registry" — while
        // ExecuteAgentTurnCommandHandler.BuildRegistrationSnapshot already treats a null owning agent as
        // zero sub-agents, so the Agents lane charged nothing for what this provider injected in full.
        var registry = new Mock<Application.AI.Common.Interfaces.IAgentMetadataRegistry>();
        registry.Setup(r => r.GetAll()).Returns(new List<Domain.AI.Agents.AgentDefinition>
        {
            new() { Id = "peer-agent", Name = "Peer Agent", Description = "Handles peer-shaped work." }
        });
        var provider = new PeerAgentContextProvider(registry.Object, owningAgentId: null);

        var result = await provider.InvokingAsync(MakeContext(ActiveInput()));

        result.Instructions.Should().NotContain("peer-agent",
            "a caller with no owning agent id has no verified self to exclude, so nothing should be " +
            "injected at all rather than the full registry");
    }

    [Theory]
    [InlineData(PlanCapabilities.LlmCall)]
    [InlineData(PlanCapabilities.Retrieval)]
    public async Task GoverningProvider_ActuallyRemovesAReservedPlanCapabilityName(string reservedName)
    {
        // This is the security assertion. The reserved-name drop is a control, and on the additive hook
        // the merge re-admits every name it drops — so the control was inert exactly when it mattered.
        var input = new AIContext
        {
            Instructions = SystemSentinel,
            Messages = new List<ChatMessage> { new(ChatRole.User, UserQuery) },
            Tools = [MakeFunctionTool(reservedName), MakeFunctionTool("file_system")]
        };

        var result = await new GoverningToolContextProvider(
                NullLogger<GoverningToolContextProvider>.Instance, Application.AI.Common.Tests.Governance.AdmissionHarness.PermissiveSanitizer())
            .InvokingAsync(MakeContext(input));

        var names = result.Tools?.Select(t => t.Name).ToList() ?? [];
        names.Should().NotContain(reservedName,
            "a name the plan engine owns must never reach the model down the AIContext.Tools channel");
        names.Should().Contain("file_system");
    }

    [Fact]
    public async Task GoverningProvider_PublishesOnlyTheGovernedCopyOfEachTool()
    {
        var result = await new GoverningToolContextProvider(
                NullLogger<GoverningToolContextProvider>.Instance, Application.AI.Common.Tests.Governance.AdmissionHarness.PermissiveSanitizer())
            .InvokingAsync(MakeContext(ActiveInput()));

        var tools = result.Tools?.ToList() ?? [];
        tools.Should().HaveCount(2, "wrapping must replace each tool, not add a second copy alongside it");
        tools.Should().AllBeOfType<GovernedAIFunction>(
            "an unwrapped original surviving next to its governed twin lets the model call the ungoverned one");
    }

    private static int CountOccurrences(string? haystack, string needle)
    {
        if (string.IsNullOrEmpty(haystack)) return 0;

        var count = 0;
        var index = haystack.IndexOf(needle, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = haystack.IndexOf(needle, index + needle.Length, StringComparison.Ordinal);
        }
        return count;
    }
}
