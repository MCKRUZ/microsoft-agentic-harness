using Application.AI.Common.Factories;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Telemetry;
using Application.AI.Common.Interfaces.Tools;
using Application.AI.Common.Services.Agent;
using Application.AI.Common.Services.Context;
using Application.AI.Common.Services.Skills;
using Application.AI.Common.Services.Tools;
using Application.AI.Common.Tests.Helpers;
using Domain.AI.Models;
using Domain.AI.Skills;
using Domain.AI.Tools;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using FluentAssertions;
using Infrastructure.AI.Telemetry.Redaction;
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

    /// <summary>
    /// The one tool the skill grants. Registered for real below, so the allowlist has something to permit
    /// as well as something to deny — a filter proven only against denials would pass while stripping
    /// everything.
    /// </summary>
    private const string AllowedTool = "file_system";

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
    /// <remarks>
    /// Governance is deliberately left unconfigured, which means the host's <c>EnforceToolInvocation</c>
    /// switch is off — the default composition, and the one that carried the #347 defect. The factory
    /// reads that switch nowhere, because whether governance is active is a per-run fact a rail built
    /// once at construction cannot know; so the rail below is the rail every host gets. Re-adding a
    /// condition on the switch would drop the governance wrapper from these expectations and fail.
    /// </remarks>
    private AgentExecutionContextFactory CreateFactory(
        bool recall = true,
        bool budgetTracker = true)
    {
        var appConfig = new AppConfig
        {
            AI = new AIConfig
            {
                AgentFramework = new AgentFrameworkConfig { DefaultDeployment = "gpt-4o" },
                Skills = new SkillsConfig { BasePath = _skillsRoot },
                KnowledgeBridge = new KnowledgeBridgeConfig { Enabled = recall },
                LearningsRecall = new LearningsRecallConfig { Enabled = recall }
            }
        };

        var services = new ServiceCollection();

        // The two recall providers are wired only when the factory can reach an ambient request scope —
        // they resolve their tenant-aware collaborator per invocation, so an empty scope is enough here.
        services.AddSingleton(Mock.Of<IAmbientRequestScope>());

        // A real tool behind the name the skill grants, so the agent is actually built holding it.
        services.AddKeyedSingleton<ITool>(AllowedTool, (_, _) => new StubTool(AllowedTool));
        var redactionFilter = TestRedactionFilter.Instance;
        services.AddSingleton<IContentRedactionFilter>(redactionFilter);
        var sp = services.BuildServiceProvider();

        return new AgentExecutionContextFactory(
            NullLogger<AgentExecutionContextFactory>.Instance,
            Mock.Of<IOptionsMonitor<AppConfig>>(m => m.CurrentValue == appConfig),
            sp,
            NullLoggerFactory.Instance,
            new ToolChainBuilder(
                NullLogger<ToolChainBuilder>.Instance, sp, new PassThroughToolConverter()),
            new SkillPrerequisiteResolver(),
            new UnsandboxedSkillFileReader(),
            budgetTracker
                ? new ContextBudgetTracker(
                    Mock.Of<IOptionsMonitor<AppConfig>>(m => m.CurrentValue == new AppConfig()),
                    NullLogger<ContextBudgetTracker>.Instance)
                : null);
    }

    private static IReadOnlyList<Type> RailTypes(Domain.AI.Agents.AgentExecutionContext context) =>
        [.. context.AIContextProviders!.Select(p => p.GetType())];

    // ── The whole rail, in order ──────────────────────────────────────────────

    [Fact]
    public async Task MapToAgentContext_EveryOptionalProviderEnabled_RailIsExactlyThisSequence()
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

    // ── Rule 2: the measurer is last, and stays last ──────────────────────────

    [Fact]
    public async Task MapToAgentContext_AppendingToTheBuiltRail_IsRefused()
    {
        // The rail leaves the factory through a settable IList property and is handed to the framework
        // as one, so no type on that path can express "this list is finished". Refusing the append is
        // therefore the instance's own job (issue #277).
        //
        // Displacement is the failure this refuses. A provider appended after the measurer pushes it out
        // of last place, and from any earlier position it charges the turn for less than the turn
        // actually cost — so the budget meant to bound a conversation stops bounding it, silently, in
        // the direction nobody notices.
        var context = await CreateFactory().MapToAgentContextAsync([MakeSkill()], new SkillAgentOptions());

        var append = () => context.AIContextProviders!.Add(new ToolPermissionFilter([AllowedTool]));

        append.Should().Throw<NotSupportedException>(
            "a consumer that appends to a finished rail must fail loudly rather than silently displace "
            + "the per-turn measurer from the last position");

        RailTypes(context)[^1].Should().Be(typeof(PerTurnBudgetContextProvider),
            "the refused append must also leave the rail as it was");
    }

    [Fact]
    public async Task MapToAgentContext_TheReadOnlyRail_IsAcceptedByTheFrameworkAgentItIsBuiltFor()
    {
        // The other half of #277. Refusing the append is only safe if the framework itself never needed to
        // mutate the rail — and nothing else in this suite would notice if a future Microsoft.Agents.AI
        // upgrade started to, because every other test reads the rail without ever handing it to a real
        // agent. That would surface in production as a NotSupportedException at agent construction.
        var context = await CreateFactory().MapToAgentContextAsync([MakeSkill()], new SkillAgentOptions());

        // Control: the instrument can fail. Without this, an agent that quietly ignored the rail entirely
        // would satisfy the assertion below just as well as one that handles it correctly.
        var append = () => context.AIContextProviders!.Add(new ToolPermissionFilter([AllowedTool]));
        append.Should().Throw<NotSupportedException>(
            "control: the rail handed over below must be one that would throw if the framework mutated it");

        // Built the way AgentFactory builds it, so this exercises the shape production actually hands over.
        using var chatClient = new Fakes.FakeChatClient().WithDefaultResponse("ok");
        var agent = new ChatClientAgent(chatClient, new ChatClientAgentOptions
        {
            Name = context.Name,
            ChatOptions = new ChatOptions { Instructions = context.Instruction },
            AIContextProviders = context.AIContextProviders
        });

        var run = async () => await agent.RunAsync("hello");

        await run.Should().NotThrowAsync(
            "the framework consumes the rail as a sequence and copies it, so handing it out read-only is "
            + "safe; if an upgrade changes that, this fails here rather than in a host's production logs");
    }

    // ── Rule 2 continued: the measurer is last, in every configuration ────────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MapToAgentContext_AnyOptionalProviderCombination_BudgetMeasurerIsLast(bool recall)
    {
        var context = await CreateFactory(recall)
            .MapToAgentContextAsync([MakeSkill()], new SkillAgentOptions());

        var rail = context.AIContextProviders!;

        // Control: the two rows really do differ, so this is not the same case asserted twice. Without
        // it, a factory that ignored the flag would satisfy both.
        rail.Count.Should().Be(4 + (recall ? 2 : 0));

        rail[^1].Should().BeOfType<PerTurnBudgetContextProvider>(
            "the measurer charges the difference between what it is handed and the baseline it was built "
            + "with, so anywhere but last it silently omits whatever follows it — the under-reporting it "
            + "exists to remove, back again and still looking like a working feature");
    }

    [Fact]
    public async Task MapToAgentContext_NoBudgetTrackerWired_RailIsTheFullyLoadedRailMinusTheMeasurer()
    {
        // A host that does not track context must get exactly the rail it had before the measurer existed —
        // not a rail with an inert provider on the end of it.
        //
        // Run fully loaded on purpose. Asserting this against a two-provider rail would pass for any
        // factory that merely omits the measurer, including one that also dropped or reordered the
        // providers that only appear when the optional features are on — the case with something to break.
        var tracked = await CreateFactory(recall: true, budgetTracker: true)
            .MapToAgentContextAsync([MakeSkill()], new SkillAgentOptions());
        var untracked = await CreateFactory(recall: true, budgetTracker: false)
            .MapToAgentContextAsync([MakeSkill()], new SkillAgentOptions());

        // Control: "minus the measurer" is only meaningful if the tracked rail ends with one. Without
        // this, a factory that appended nothing at all would satisfy the assertion below.
        RailTypes(tracked)[^1].Should().Be(typeof(PerTurnBudgetContextProvider));

        // Derived from the tracked rail rather than restated, so the sequence has one home. Restating it
        // would let the two drift apart and still pass, which is the thing this test exists to notice.
        RailTypes(untracked).Should().Equal(
            RailTypes(tracked).SkipLast(1),
            "an untracked host gets the rail unchanged, not a rail that quietly differs in some other way");
    }

    // ── Rule 1: the filter's decision survives to the end of the rail ─────────

    [Fact]
    public async Task MapToAgentContext_FullyLoadedRail_NoProviderBelowTheFilterReintroducesADeniedTool()
    {
        var context = await CreateFactory().MapToAgentContextAsync([MakeSkill()], new SkillAgentOptions());

        // Control. run_skill_script is contributed by the skills provider and is deliberately NOT exempt
        // from the allowlist (unlike load_skill and read_skill_resource), so the filter must strip it. If it
        // never appeared above the filter, the assertion below would pass against a rail that filters
        // nothing at all.
        // Driven by identity, not by index: the control must keep holding under exactly the misordering
        // this test exists to catch, or a broken rail would fail here and never reach the assertion below.
        var contributed = await AIContextRailDriver.DriveAsync(context.AIContextProviders!.OfType<AgentSkillsProvider>(), context);
        ToolNames(contributed).Should().Contain(
            AgentSkillsProvider.RunSkillScriptToolName,
            "control: the rail must actually offer a tool the allowlist denies, or this test proves nothing");

        var finished = await AIContextRailDriver.DriveAsync(context.AIContextProviders!, context);

        // The security property, read at the end of the rail rather than at the filter. A tool-contributing
        // provider inserted below the filter would surface here and nowhere else.
        ToolNames(finished).Should().OnlyContain(
            name => name == AllowedTool
                || ToolPermissionFilter.SkillDisclosureToolNames.Contains(name),
            "every tool the model is finally offered must be one the allowlist grants or one of the two "
            + "exempt skill-content tools; anything else was contributed after the filter had already run");

        // The other half of the same property: a filter that stripped everything would satisfy the
        // assertion above. The granted tool and the two exempt ones must all still be there.
        ToolNames(finished).Should().Contain(
            [AllowedTool,
             AgentSkillsProvider.LoadSkillToolName,
             AgentSkillsProvider.ReadSkillResourceToolName],
            "the filter denies; it must not also deny what the skill granted, or progressive disclosure "
            + "and the agent's own capability both die silently");
    }

    private static IEnumerable<string> ToolNames(AIContext context) =>
        context.Tools?.Select(t => t.Name) ?? [];
}
