using Application.AI.Common.Factories;
using Application.AI.Common.Helpers;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Context;
using Application.AI.Common.Interfaces.Skills;
using Application.AI.Common.Interfaces.Telemetry;
using Application.AI.Common.Services.Agent;
using Application.AI.Common.Services.Context;
using Application.AI.Common.Services.Skills;
using Application.AI.Common.Services.Tools;
using Application.AI.Common.Tests.Helpers;
using Domain.AI.Skills;
using Domain.AI.Telemetry.Conventions;
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
/// Pins the harness's documented three-tier progressive skill disclosure end to end against the real
/// <see cref="AgentExecutionContextFactory"/> wiring: the system prompt carries only the Tier 1 index
/// card, and the Tier 2 <c>load_skill</c> tool the model must call instead is actually reachable.
/// </summary>
/// <remarks>
/// <para>
/// These three assertions are one mechanism, not three independent checks, which is why they live in one
/// class. Removing the eager body from the prompt is only safe if <c>load_skill</c> can complete, and
/// <c>load_skill</c> has to clear two independent gates to do so — the framework's approval wrapper and
/// the harness's own <see cref="ToolPermissionFilter"/>. A regression in either gate silently converts
/// "loads on demand" into "never loads", with no exception and no log: the model simply never receives
/// the instructions. Asserting the prompt shape alone would not catch that.
/// </para>
/// <para>
/// A fourth assertion belongs to the same mechanism: the provider must advertise the agent's <em>own</em>
/// skills and nothing else. Registering skills explicitly is what enforces that, and the failure it
/// prevents is invisible from the prompt alone — a skill the agent was never granted looks identical to
/// one it was, right up until the model loads it.
/// </para>
/// <para>
/// The skill directories the fixture writes are real but incidental here: the provider is handed skills
/// directly rather than a directory to search. They exist so the over-disclosure test can prove that a
/// skill's mere presence on the host no longer makes it loadable.
/// </para>
/// </remarks>
public sealed class AgentExecutionContextFactoryProgressiveDisclosureTests : IDisposable
{
    /// <summary>
    /// Sentinel that appears only in the skill body, never in its name or description. Any assertion
    /// about "the full body reached the prompt" keys off this string.
    /// </summary>
    private const string BodyMarker = "MARKER_TIER2_BODY_ONLY";

    private const string SkillName = "demo-skill";
    private const string SkillDescription = "A demo skill used to pin progressive disclosure.";

    /// <summary>A valid skill present on the host that this agent is never assigned.</summary>
    private const string UnassignedSkillName = "other-skill";

    /// <summary>Sentinel that appears only inside the skill's reference file.</summary>
    private const string ReferenceMarker = "MARKER_TIER3_REFERENCE_ONLY";

    private readonly SkillDirectoryFixture _skills = new("progdisc");
    private readonly string _skillsRoot;
    private readonly string _skillDir;

    public AgentExecutionContextFactoryProgressiveDisclosureTests()
    {
        // The fixture owns the loader's acceptance rule (frontmatter name == directory name, description
        // present). Getting that wrong here would make the provider yield no skills and turn every
        // assertion below into a vacuous pass, which is why it is not hand-rolled.
        _skillDir = _skills.CreateSkill(
            Path.Combine("skills", SkillName),
            $"# Demo Skill\n\n{BodyMarker}\n\nFollow the demo procedure precisely.",
            SkillDescription);
        _skillsRoot = Path.GetDirectoryName(_skillDir)!;
    }

    public void Dispose() => _skills.Dispose();

    /// <summary>
    /// The skill as the harness parser would produce it: <see cref="SkillDefinition.Instructions"/> holds
    /// the full body (that is what the parser assigns today), and an <c>allowed-tools</c> declaration is
    /// present so the factory wires a real <see cref="ToolPermissionFilter"/>. Every shipped SKILL.md that
    /// declares tools lands in exactly this shape.
    /// </summary>
    private SkillDefinition MakeSkill() => new()
    {
        Id = SkillName,
        Name = SkillName,
        Description = SkillDescription,
        Instructions = $"# Demo Skill\n\n{BodyMarker}\n\nFollow the demo procedure precisely.",
        BaseDirectory = _skillDir,
        AllowedTools = ["file_system"]
    };

    private AgentExecutionContextFactory CreateFactory(IContextBudgetTracker? budgetTracker = null)
    {
        var appConfig = new AppConfig
        {
            AI = new AIConfig
            {
                AgentFramework = new AgentFrameworkConfig { DefaultDeployment = "gpt-4o" },
                Skills = new SkillsConfig { BasePath = _skillsRoot },
            }
        };
        var monitor = Mock.Of<IOptionsMonitor<AppConfig>>(m => m.CurrentValue == appConfig);
        var services = new ServiceCollection();
        services.AddSingleton<IContentRedactionFilter>(TestRedactionFilter.Instance);
        var sp = services.BuildServiceProvider();

        return new AgentExecutionContextFactory(
            NullLogger<AgentExecutionContextFactory>.Instance,
            monitor,
            sp,
            NullLoggerFactory.Instance,
            new ToolChainBuilder(
                NullLogger<ToolChainBuilder>.Instance, sp),
            new SkillPrerequisiteResolver(),
            new UnsandboxedSkillFileReader(),
            Application.AI.Common.Tests.Governance.AdmissionHarness.PermissiveSanitizer(),
            Mock.Of<IAgentMetadataRegistry>(r => r.GetAll() == new List<Domain.AI.Agents.AgentDefinition>()),
            budgetTracker);
    }

    private static ContextBudgetTracker CreateBudgetTracker() => new(
        Mock.Of<IOptionsMonitor<AppConfig>>(m => m.CurrentValue == new AppConfig()),
        NullLogger<ContextBudgetTracker>.Instance);

    /// <summary>
    /// The agent name the factory derives from the skill name, which is the key the budget is filed under.
    /// </summary>
    private const string AgentName = "DemoSkillAgent";

    /// <summary>
    /// Drives the framework skills provider exactly as the agent runtime does and returns the
    /// <see cref="AIContext"/> it contributes — its instructions block and its three skill tools.
    /// </summary>
    private static async Task<AIContext> InvokeSkillsProviderAsync(AgentSkillsProvider provider)
    {
        var invoking = new AIContextProvider.InvokingContext(
            new Mock<AIAgent>().Object,
            new Mock<AgentSession>().Object,
            new AIContext());

        return await provider.InvokingAsync(invoking);
    }

    private static AgentSkillsProvider SkillsProviderOf(Domain.AI.Agents.AgentExecutionContext context) =>
        context.AIContextProviders!.OfType<AgentSkillsProvider>().Single();

    /// <summary>
    /// Drives the agent's whole context-provider rail the way the runtime does, via the shared
    /// <see cref="AIContextRailDriver"/>.
    /// </summary>
    /// <remarks>
    /// Invoking one provider in isolation cannot exercise per-turn accounting, because the measurer sits at
    /// the end of the chain and only sees what the providers ahead of it accumulated.
    /// </remarks>
    private static Task<AIContext> DriveRailAsync(Domain.AI.Agents.AgentExecutionContext context) =>
        AIContextRailDriver.DriveAsync(context);

    // ── Tier 1: the prompt carries the index card, not the body ──────────────

    [Fact]
    public async Task MapToAgentContext_SkillCoveredByProvider_DoesNotBakeFullBodyIntoSystemPrompt()
    {
        var context = await CreateFactory().MapToAgentContextAsync([MakeSkill()], new SkillAgentOptions());

        context.Instruction.Should().NotContain(
            BodyMarker,
            "the skill body is Tier 2 content — it must arrive via load_skill on demand, not be baked " +
            "into the static system prompt on every turn while the provider offers the same body again");
    }

    [Fact]
    public async Task MapToAgentContext_SkillCoveredByProvider_RecordsThatItsBodyWasHeldBack()
    {
        // The producer half of #507's fix. The test above proves the body is not in the prompt; this
        // proves the context SAYS SO, which is a separate thing and the one anything downstream can
        // act on. The instruction is one opaque string — nothing in it distinguishes a body that was
        // folded in from one that was held back — so a consumer sizing the prompt has to be told.
        //
        // Without this, deleting the assignment in the factory leaves every test in the suite green
        // while the context bar silently returns to charging the skills lane for text the model never
        // received and deflating the system lane to match: the exact symptom #507 was filed about.
        var context = await CreateFactory().MapToAgentContextAsync([MakeSkill()], new SkillAgentOptions());

        context.DisclosedOnDemandSkillIds.Should().NotBeNull()
            .And.Contain(SkillName,
                "a skill whose body the provider serves on demand must be reported as held back");
    }

    [Fact]
    public async Task MapToAgentContext_SkillCoveredByProvider_StillAdvertisesSkillAsIndexCard()
    {
        var context = await CreateFactory().MapToAgentContextAsync([MakeSkill()], new SkillAgentOptions());
        var aiContext = await InvokeSkillsProviderAsync(SkillsProviderOf(context));

        aiContext.Instructions.Should().Contain(SkillName)
            .And.Contain(SkillDescription,
                "dropping the body only works if the model can still discover the skill exists");
    }

    // ── Tier 2 gate 1: the framework's approval wrapper ───────────────────────

    [Fact]
    public async Task MapToAgentContext_LoadSkillTool_IsInvocableWithoutAnApprovalRoundTrip()
    {
        var context = await CreateFactory().MapToAgentContextAsync([MakeSkill()], new SkillAgentOptions());
        var aiContext = await InvokeSkillsProviderAsync(SkillsProviderOf(context));

        var loadSkill = aiContext.Tools!
            .OfType<AIFunction>()
            .Single(t => t.Name == AgentSkillsProvider.LoadSkillToolName);

        loadSkill.Should().NotBeOfType<ApprovalRequiredAIFunction>(
            "the framework gates load_skill behind human approval by default, and no turn-driver in this " +
            "harness answers ToolApprovalRequestContent — so an approval-wrapped load_skill can never " +
            "complete and the model would never receive any skill instructions");
    }

    // ── Tier 2 gate 2: the harness's own tool allow-list ──────────────────────

    [Fact]
    public async Task MapToAgentContext_SkillDeclaresAllowedTools_LoadSkillSurvivesToolPermissionFilter()
    {
        var context = await CreateFactory().MapToAgentContextAsync([MakeSkill()], new SkillAgentOptions());
        var providers = context.AIContextProviders!;

        var skillTools = (await InvokeSkillsProviderAsync(SkillsProviderOf(context))).Tools!;
        var filter = providers.OfType<ToolPermissionFilter>().Single();

        // Reproduces the documented provider ordering: the filter runs after the skills provider and
        // sees the accumulated tool set, framework-injected tools included.
        var filtered = await filter.InvokingAsync(new AIContextProvider.InvokingContext(
            new Mock<AIAgent>().Object,
            new Mock<AgentSession>().Object,
            new AIContext { Tools = [.. skillTools] }));

        filtered.Tools.Should().Contain(
            t => t.Name == AgentSkillsProvider.LoadSkillToolName,
            "a skill declaring allowed-tools drives an allow-list that never contains the framework's own " +
            "skill tools, so the filter strips load_skill and progressive disclosure dies for exactly the " +
            "skills that declare tool restrictions");
    }

    // ── Confinement: only the agent's own skills are reachable ────────────────

    [Fact]
    public async Task MapToAgentContext_SkillOnHostButNotAssigned_IsNeitherAdvertisedNorLoadable()
    {
        // A second, entirely valid skill sitting under the same configured root — the shape that a
        // directory-scanning provider would happily hand to any agent that asked for it.
        _skills.CreateSkill(
            Path.Combine("skills", UnassignedSkillName),
            "# Other Skill\n\nUNASSIGNED_BODY",
            "A skill this agent was never granted.");

        var context = await CreateFactory().MapToAgentContextAsync([MakeSkill()], new SkillAgentOptions());
        var aiContext = await InvokeSkillsProviderAsync(SkillsProviderOf(context));
        var loadSkill = aiContext.Tools!
            .OfType<AIFunction>()
            .Single(t => t.Name == AgentSkillsProvider.LoadSkillToolName);

        // Positive control first. Without it, a provider holding zero skills would satisfy every negative
        // assertion below and the test would pass while proving nothing.
        var assigned = await loadSkill.InvokeAsync(new AIFunctionArguments { ["skillName"] = SkillName });
        assigned?.ToString().Should().Contain(
            BodyMarker,
            "the agent's own skill must still load on demand — otherwise this test's negative assertions " +
            "are vacuous and would pass against a provider that serves nothing at all");

        aiContext.Instructions.Should().NotContain(
            UnassignedSkillName,
            "the index card must list only the skills this agent was assigned; advertising a skill it was " +
            "never granted invites the model to load capability that was deliberately withheld");

        var unassigned = await loadSkill.InvokeAsync(new AIFunctionArguments { ["skillName"] = UnassignedSkillName });
        unassigned?.ToString().Should().Contain(
            "not found",
            "a skill's presence on the host must not make it loadable — assignment is the grant, and the " +
            "provider is the only thing enforcing it");
    }

    // ── Tier 3: supporting files are advertised and readable ──────────────────

    [Fact]
    public async Task MapToAgentContext_SkillWithReference_AdvertisesAndServesItOnDemand()
    {
        var referencePath = Path.Combine(_skillDir, "references", "guide.md");
        Directory.CreateDirectory(Path.GetDirectoryName(referencePath)!);
        await File.WriteAllTextAsync(referencePath, ReferenceMarker);

        var skill = MakeSkill();
        skill.References.Add(new SkillResource
        {
            FileName = "guide.md",
            RelativePath = "references/guide.md",
            FilePath = referencePath,
            ResourceType = SkillResourceType.Reference
        });

        var context = await CreateFactory().MapToAgentContextAsync([skill], new SkillAgentOptions());
        var aiContext = await InvokeSkillsProviderAsync(SkillsProviderOf(context));
        var tools = aiContext.Tools!.OfType<AIFunction>().ToList();

        var body = await tools
            .Single(t => t.Name == AgentSkillsProvider.LoadSkillToolName)
            .InvokeAsync(new AIFunctionArguments { ["skillName"] = SkillName });

        body?.ToString().Should().Contain(
            "references/guide.md",
            "a resource the model is never told about is a resource it will never read — the skill body " +
            "carries the authoritative list");

        // read_skill_resource takes an IServiceProvider parameter and the function factory refuses to bind
        // it from a null Services — the agent runtime supplies one, so the test must too.
        var readArguments = new AIFunctionArguments
        {
            ["skillName"] = SkillName,
            ["resourceName"] = "references/guide.md"
        };
        readArguments.Services = new ServiceCollection().BuildServiceProvider();

        var resource = await tools
            .Single(t => t.Name == AgentSkillsProvider.ReadSkillResourceToolName)
            .InvokeAsync(readArguments);

        resource?.ToString().Should().Contain(
            ReferenceMarker,
            "Tier 3 exists so bulk reference material stays out of the prompt until asked for; if the read " +
            "does not return the file's contents, that material is simply unreachable");
    }

    // ── The budget sees what the tiers defer ──────────────────────────────────

    [Fact]
    public async Task LoadSkill_ChargesTheBodyItServedToTheContextBudget()
    {
        var budget = CreateBudgetTracker();
        var context = await CreateFactory(budget).MapToAgentContextAsync([MakeSkill()], new SkillAgentOptions());
        var aiContext = await InvokeSkillsProviderAsync(SkillsProviderOf(context));

        var beforeLoad = budget.GetBreakdown(AgentName);
        var body = await aiContext.Tools!
            .OfType<AIFunction>()
            .Single(t => t.Name == AgentSkillsProvider.LoadSkillToolName)
            .InvokeAsync(new AIFunctionArguments { ["skillName"] = SkillName });

        // Positive control: without this, a provider that served nothing would satisfy the assertion below
        // by charging nothing, and the test would pass while proving the opposite of what it claims.
        body?.ToString().Should().Contain(BodyMarker, "the load must actually have served the body");

        // Captured after the provider composed its index card but before load_skill ran. This is the
        // control for the assertion that follows: it proves the charge tracks the model's pull and not
        // merely the provider being invoked, which would over-report on every turn — the inverse of the
        // bug being fixed, and just as wrong.
        beforeLoad.Should().NotContainKey(
            BudgetChargingSkill.Tier2Component,
            "Tier 2 is deferred — building the index card reads the frontmatter, never the body, so " +
            "nothing is owed for it until the model actually asks");
        budget.GetBreakdown(AgentName).Should().ContainKey(
            BudgetChargingSkill.Tier2Component,
            "the tokens the body just put into the context are spent whether or not the harness counts " +
            "them; uncounted, the budget under-reports worst on the turns that load the most skills")
            .WhoseValue.Should().Be(TokenEstimationHelper.EstimateTokens(body?.ToString()));
    }

    [Fact]
    public async Task TheRail_ChargesItsTier1IndexCardToTheContextBudget_OnEveryTurn()
    {
        var budget = CreateBudgetTracker();
        var context = await CreateFactory(budget).MapToAgentContextAsync([MakeSkill()], new SkillAgentOptions());

        // What the skills provider contributes on its own: the Tier 1 index card. Measured from an empty
        // input so it is the card alone, and used below as an independent expectation rather than a
        // restatement of the measurer's own arithmetic.
        var indexCard = await InvokeSkillsProviderAsync(SkillsProviderOf(context));
        indexCard.Instructions.Should().Contain(
            SkillName, "control: a provider advertising nothing would make every assertion below vacuous");

        // Control. Building the agent charges the static prompt and the tool schemas; nothing on the rail
        // has run yet, so a per-turn charge appearing here would mean the measurer bills at construction —
        // the opposite error, and equally wrong.
        budget.GetBreakdown(AgentName).Should().NotContainKey(
            ContextConventions.BudgetComponents.PerTurnContext);

        await DriveRailAsync(context);
        var afterOneTurn = budget.GetBreakdown(AgentName)[ContextConventions.BudgetComponents.PerTurnContext];

        afterOneTurn.Should().BeGreaterThanOrEqualTo(
            TokenEstimationHelper.EstimateTokens(indexCard.Instructions),
            "the index card is composed by the framework and injected on every turn; uncounted, the budget " +
            "under-reports by its full size on each one");

        await DriveRailAsync(context);

        budget.GetBreakdown(AgentName)[ContextConventions.BudgetComponents.PerTurnContext]
            .Should().Be(afterOneTurn * 2,
                "this cost recurs — charging it once would leave the reported budget drifting further from " +
                "the real context the longer a conversation runs, which is the defect, not the fix");
    }

    [Fact]
    public async Task TheRail_ChargesTheSameAmount_HoweverLargeTheStaticSystemPromptIs()
    {
        // Two agents differing only in the size of their static prompt. The rail contributes the same
        // index card to both, so the per-turn charge must be identical — that is what "the baseline is
        // excluded" means, stated without restating the measurer's own arithmetic.
        var longPrompt = string.Join(" ", Enumerable.Repeat("STATIC-PROMPT-FILLER", 200));

        var withoutPrompt = CreateBudgetTracker();
        var withPrompt = CreateBudgetTracker();

        var bare = await CreateFactory(withoutPrompt)
            .MapToAgentContextAsync([MakeSkill()], new SkillAgentOptions());
        var padded = await CreateFactory(withPrompt)
            .MapToAgentContextAsync([MakeSkill()], new SkillAgentOptions { AgentInstructions = longPrompt });

        // Control: the two really do differ where the test claims they differ. Without this, two agents
        // that both ended up with an empty prompt would satisfy the equality below and prove nothing.
        TokenEstimationHelper.EstimateTokens(padded.Instruction).Should().BeGreaterThan(
            TokenEstimationHelper.EstimateTokens(bare.Instruction) + 1000);

        await DriveRailAsync(bare);
        await DriveRailAsync(padded);

        var component = ContextConventions.BudgetComponents.PerTurnContext;
        withPrompt.GetBreakdown(AgentName)[component].Should().Be(
            withoutPrompt.GetBreakdown(AgentName)[component],
            "the measurer sees the static prompt in the accumulated context on every turn; charging what " +
            "it sees rather than what the rail added would re-bill the whole prompt each turn — a larger " +
            "error than the one being fixed, and one that would still look like a working feature");
    }

    [Fact]
    public async Task ReadSkillResource_ChargesTheFileItServedToTheContextBudget()
    {
        var referencePath = Path.Combine(_skillDir, "references", "guide.md");
        Directory.CreateDirectory(Path.GetDirectoryName(referencePath)!);
        await File.WriteAllTextAsync(referencePath, ReferenceMarker);

        var skill = MakeSkill();
        skill.References.Add(new SkillResource
        {
            FileName = "guide.md",
            RelativePath = "references/guide.md",
            FilePath = referencePath,
            ResourceType = SkillResourceType.Reference
        });

        var budget = CreateBudgetTracker();
        var context = await CreateFactory(budget).MapToAgentContextAsync([skill], new SkillAgentOptions());
        var aiContext = await InvokeSkillsProviderAsync(SkillsProviderOf(context));

        var readArguments = new AIFunctionArguments
        {
            ["skillName"] = SkillName,
            ["resourceName"] = "references/guide.md"
        };
        readArguments.Services = new ServiceCollection().BuildServiceProvider();

        var resource = await aiContext.Tools!
            .OfType<AIFunction>()
            .Single(t => t.Name == AgentSkillsProvider.ReadSkillResourceToolName)
            .InvokeAsync(readArguments);

        resource?.ToString().Should().Contain(ReferenceMarker, "the read must actually have served the file");
        budget.GetBreakdown(AgentName).Should().ContainKey(
            BudgetChargingSkill.Tier3Component,
            "Tier 3 is where the bulk lives — supporting files are exactly the material progressive " +
            "disclosure defers, so a budget blind to them is blind to the largest on-demand cost");
    }
}
