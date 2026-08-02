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
}
