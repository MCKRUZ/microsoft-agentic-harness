using Application.AI.Common.Factories;
using Application.AI.Common.Interfaces.Skills;
using Application.AI.Common.Services.Agent;
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
/// The skill on disk is deliberately minimal but real. <c>AgentFileSkillsSource</c> rejects any skill
/// whose frontmatter <c>name</c> does not match its containing directory name (ordinal), so the fixture
/// writes <c>demo-skill/SKILL.md</c> with a matching name — a mismatch would make the provider silently
/// yield no skills and turn every assertion below into a vacuous pass.
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

    private AgentExecutionContextFactory CreateFactory()
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
        var sp = new ServiceCollection().BuildServiceProvider();

        return new AgentExecutionContextFactory(
            NullLogger<AgentExecutionContextFactory>.Instance,
            monitor,
            sp,
            NullLoggerFactory.Instance,
            new ToolChainBuilder(NullLogger<ToolChainBuilder>.Instance, sp),
            new SkillPrerequisiteResolver());
    }

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
}
