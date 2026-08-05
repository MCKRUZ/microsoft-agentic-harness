using Application.AI.Common.Factories;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Context;
using Application.AI.Common.Services.Agent;
using Application.AI.Common.Services.Context;
using Application.AI.Common.Services.Skills;
using Application.AI.Common.Services.Tools;
using Application.AI.Common.Tests.Helpers;
using Domain.AI.Skills;
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

namespace Application.AI.Common.Tests.Factories;

/// <summary>
/// Pins the <em>position</em> of every provider on the agent's <see cref="AIContextProvider"/> rail, and the
/// two behaviours that positions decide.
/// </summary>
/// <remarks>
/// <para>
/// The runtime feeds each provider the accumulated output of the ones before it, so where a provider sits
/// is what it can see and what it can change. Two rules follow, and both are load-bearing:
/// </para>
/// <list type="number">
///   <item>
///     Every tool-contributing provider sits <em>above</em> <see cref="ToolPermissionFilter"/>. One added
///     below it contributes tools the filter has already finished filtering — an agent calling a tool its
///     allowlist was written to deny, with nothing thrown and nothing logged.
///   </item>
///   <item>
///     <see cref="PerTurnBudgetContextProvider"/> is <em>last</em>. Only the final position sees everything
///     the rest contributed; anywhere else it silently under-charges by whatever follows it.
///   </item>
/// </list>
/// <para>
/// Neither rule was asserted anywhere before this class. Every other test that reads the rail does so with
/// <c>OfType&lt;T&gt;().Single()</c>, which finds its provider at any index and therefore passes whatever
/// the order happens to be — including an order that has broken rule 1. This is the assembly whose
/// <c>CLAUDE.md</c> records the additive-hook provider defect shipping <em>four separate times</em>, each
/// time with green unit tests, so a rule held up only by a code comment is a rule waiting to be broken.
/// </para>
/// <para>
/// The exact-sequence assertion is deliberate rather than lazy: a test that checked only relative pairs
/// would let a new provider be slotted in anywhere without comment. Failing on any insertion forces whoever
/// adds one to state where it goes and why, which is the decision that actually needs review.
/// </para>
/// </remarks>
public sealed class AgentExecutionContextFactoryRailOrderTests : IDisposable
{
    private const string SkillName = "rail-skill";
    private const string AllowedTool = "file_system";

    /// <summary>The agent name the factory derives from <see cref="SkillName"/>.</summary>
    private const string AgentName = "RailSkillAgent";

    private readonly SkillDirectoryFixture _skills = new("railorder");
    private readonly string _skillDir;
    private readonly string _skillsRoot;

    public AgentExecutionContextFactoryRailOrderTests()
    {
        _skillDir = _skills.CreateSkill(
            Path.Combine("skills", SkillName),
            "# Rail Skill\n\nRAIL_BODY",
            "A skill used to pin context-provider rail order.");
        _skillsRoot = Path.GetDirectoryName(_skillDir)!;
    }

    public void Dispose() => _skills.Dispose();

    /// <summary>
    /// A skill in the shape that wires the most providers: a real directory so it is disclosable, and an
    /// <c>allowed-tools</c> declaration so a real <see cref="ToolPermissionFilter"/> appears.
    /// </summary>
    private SkillDefinition MakeSkill() => new()
    {
        Id = SkillName,
        Name = SkillName,
        Description = "A skill used to pin context-provider rail order.",
        Instructions = "# Rail Skill\n\nRAIL_BODY",
        BaseDirectory = _skillDir,
        AllowedTools = [AllowedTool]
    };

    /// <summary>
    /// Builds the factory with the optional providers switched on or off individually, so the tests can
    /// assert what holds across configurations rather than only in the fully-loaded one.
    /// </summary>
    private AgentExecutionContextFactory CreateFactory(
        bool recall = true,
        bool governance = true,
        IContextBudgetTracker? budgetTracker = null)
    {
        var appConfig = new AppConfig
        {
            AI = new AIConfig
            {
                AgentFramework = new AgentFrameworkConfig { DefaultDeployment = "gpt-4o" },
                Skills = new SkillsConfig { BasePath = _skillsRoot },
                KnowledgeBridge = new KnowledgeBridgeConfig { Enabled = recall },
                LearningsRecall = new LearningsRecallConfig { Enabled = recall },
                Governance = new GovernanceConfig { EnforceToolInvocation = governance }
            }
        };

        var services = new ServiceCollection();

        // The two recall providers are wired only when the factory can reach an ambient request scope —
        // they resolve their tenant-aware collaborator per invocation, so an empty scope is enough here.
        services.AddSingleton(Mock.Of<IAmbientRequestScope>());
        var sp = services.BuildServiceProvider();

        return new AgentExecutionContextFactory(
            NullLogger<AgentExecutionContextFactory>.Instance,
            Mock.Of<IOptionsMonitor<AppConfig>>(m => m.CurrentValue == appConfig),
            sp,
            NullLoggerFactory.Instance,
            new ToolChainBuilder(NullLogger<ToolChainBuilder>.Instance, sp),
            new SkillPrerequisiteResolver(),
            new UnsandboxedSkillFileReader(),
            budgetTracker ?? CreateBudgetTracker());
    }

    private static ContextBudgetTracker CreateBudgetTracker() => new(
        Mock.Of<IOptionsMonitor<AppConfig>>(m => m.CurrentValue == new AppConfig()),
        NullLogger<ContextBudgetTracker>.Instance);

    private static IReadOnlyList<Type> RailTypes(Domain.AI.Agents.AgentExecutionContext context) =>
        [.. context.AIContextProviders!.Select(p => p.GetType())];

    // ── The whole rail, in order ──────────────────────────────────────────────

    [Fact]
    public async Task Rail_WithEveryOptionalProviderEnabled_IsExactlyThisSequence()
    {
        var context = await CreateFactory().MapToAgentContextAsync([MakeSkill()], new SkillAgentOptions());

        RailTypes(context).Should().Equal(
            [
                typeof(AgentSkillsProvider),              // contributes the framework's skill tools
                typeof(ToolPermissionFilter),             // everything above it is filtered; nothing below is
                typeof(KnowledgeMemoryContextProvider),   // instructions only
                typeof(LearningsRecallContextProvider),   // instructions only
                typeof(GoverningToolContextProvider),     // wraps the finished, filtered tool set
                typeof(PerTurnBudgetContextProvider)      // measures everything above it, so it is last
            ],
            "position on this rail is behaviour: each provider sees only what the ones above it produced, "
            + "so an insertion in the wrong place changes which tools the agent can call or what its budget "
            + "records. If this list needs to change, the change is the thing to review — not the test");
    }

    // ── Rule 2: the measurer is last, in every configuration ──────────────────

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task Rail_TheBudgetMeasurerIsLast_WhicheverOptionalProvidersAreOn(bool recall, bool governance)
    {
        var context = await CreateFactory(recall, governance)
            .MapToAgentContextAsync([MakeSkill()], new SkillAgentOptions());

        var rail = context.AIContextProviders!;

        // Control: the configurations really do differ, so this is four cases and not the same one asserted
        // four times. Without it, a factory that ignored both flags would satisfy every row.
        rail.Count.Should().Be(3 + (recall ? 2 : 0) + (governance ? 1 : 0));

        rail[^1].Should().BeOfType<PerTurnBudgetContextProvider>(
            "the measurer charges the difference between what it is handed and the baseline it was built "
            + "with, so anywhere but last it silently omits whatever follows it — the under-reporting it "
            + "exists to remove, back again and still looking like a working feature");
    }

    [Fact]
    public async Task Rail_WithNoBudgetTrackerWired_IsTheSameRailWithoutAMeasurer()
    {
        // A host that does not track context must get exactly the rail it had before the measurer existed —
        // not a rail with an inert provider on the end of it.
        var factory = new AgentExecutionContextFactory(
            NullLogger<AgentExecutionContextFactory>.Instance,
            Mock.Of<IOptionsMonitor<AppConfig>>(m => m.CurrentValue == new AppConfig
            {
                AI = new AIConfig
                {
                    AgentFramework = new AgentFrameworkConfig { DefaultDeployment = "gpt-4o" },
                    Skills = new SkillsConfig { BasePath = _skillsRoot }
                }
            }),
            new ServiceCollection().BuildServiceProvider(),
            NullLoggerFactory.Instance,
            new ToolChainBuilder(
                NullLogger<ToolChainBuilder>.Instance, new ServiceCollection().BuildServiceProvider()),
            new SkillPrerequisiteResolver(),
            new UnsandboxedSkillFileReader());

        var context = await factory.MapToAgentContextAsync([MakeSkill()], new SkillAgentOptions());

        RailTypes(context).Should().Equal([typeof(AgentSkillsProvider), typeof(ToolPermissionFilter)]);
    }

    // ── Rule 1: the filter's decision survives to the end of the rail ─────────

    [Fact]
    public async Task Rail_NoProviderBelowTheFilterReintroducesADeniedTool()
    {
        var context = await CreateFactory().MapToAgentContextAsync([MakeSkill()], new SkillAgentOptions());

        // Control. run_skill_script is contributed by the skills provider and is deliberately NOT exempt
        // from the allowlist (unlike load_skill and read_skill_resource), so the filter must strip it. If it
        // never appeared above the filter, the assertion below would pass against a rail that filters
        // nothing at all.
        // Driven by identity, not by index: the control must keep holding under exactly the misordering
        // this test exists to catch, or a broken rail would fail here and never reach the assertion below.
        var contributed = await DriveAsync(context.AIContextProviders!.OfType<AgentSkillsProvider>(), context);
        ToolNames(contributed).Should().Contain(
            AgentSkillsProvider.RunSkillScriptToolName,
            "control: the rail must actually offer a tool the allowlist denies, or this test proves nothing");

        var finished = await DriveAsync(context.AIContextProviders!, context);

        // The security property, read at the end of the rail rather than at the filter. A tool-contributing
        // provider inserted below the filter would surface here and nowhere else.
        ToolNames(finished).Should().OnlyContain(
            name => name == AllowedTool
                || ToolPermissionFilter.SkillDisclosureToolNames.Contains(name),
            "every tool the model is finally offered must be one the allowlist grants or one of the two "
            + "exempt skill-content tools; anything else was contributed after the filter had already run");
    }

    private static IEnumerable<string> ToolNames(AIContext context) =>
        context.Tools?.Select(t => t.Name) ?? [];

    /// <summary>
    /// Drives <paramref name="providers"/> the way the runtime does — seeded with the agent's own
    /// instructions and tools, then feeding each provider the previous one's output.
    /// </summary>
    private static async Task<AIContext> DriveAsync(
        IEnumerable<AIContextProvider> providers,
        Domain.AI.Agents.AgentExecutionContext context)
    {
        var current = new AIContext
        {
            Instructions = context.Instruction,
            Messages = new List<ChatMessage> { new(ChatRole.User, "go") },
            Tools = context.Tools is null ? [] : [.. context.Tools]
        };

        foreach (var provider in providers)
        {
            current = await provider.InvokingAsync(new AIContextProvider.InvokingContext(
                new Mock<AIAgent>().Object, new Mock<AgentSession>().Object, current));
        }

        return current;
    }
}
