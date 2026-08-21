using Application.AI.Common.Factories;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Telemetry;
using Application.AI.Common.Interfaces.Tools;
using Application.AI.Common.Services.Agent;
using Application.AI.Common.Services.Context;
using Application.AI.Common.Services.Governance;
using Application.AI.Common.Services.Skills;
using Application.AI.Common.Services.Tools;
using Application.AI.Common.Tests.Governance;
using Application.AI.Common.Tests.Helpers;
using Domain.AI.Bundles;
using Domain.AI.Governance;
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
/// Pins where <see cref="GoverningToolContextProvider"/> draws its line on a bundle run: a
/// <em>capability</em> offered through the <see cref="AIContextProvider"/> channel must reach the
/// admission chain even when the host's global enforcement switch is off, while the two tools that
/// merely carry skill content must not.
/// </summary>
/// <remarks>
/// <para>
/// Two channels put tools in front of the model. Most arrive through <c>ToolChainBuilder</c>, which wraps
/// them for governance on the way past. The framework's own progressive-disclosure tools do not: they are
/// contributed by <see cref="AgentSkillsProvider"/>, exist only on the provider rail, and so this wrapper
/// is the only thing that can gate them.
/// </para>
/// <para>
/// A bundle run is the flow that makes the distinction matter. It executes an externally-authored agent
/// under a per-caller capability envelope naming the tools that caller may invoke, and enforcement is
/// armed by the envelope's presence rather than by the host's opt-in
/// (<see cref="GovernanceEnforcement.IsActive"/>). Before issue #347 the rail read the global switch
/// alone, so on a default composition <em>everything</em> on this channel was published unwrapped —
/// including <c>run_skill_script</c>, which executes a bundle author's script.
/// </para>
/// <para>
/// The line is not "wrap everything". An envelope grants domain tools, so a governed <c>load_skill</c> or
/// <c>read_skill_resource</c> would be refused for want of a grant no operator writes, and the bundle
/// would lose the instructions of the skills it shipped with — a failure that produces a working-looking
/// agent missing half its prompt. Those two are therefore exempt, using the same set
/// <see cref="ToolPermissionFilter"/> already exempts them by. <c>run_skill_script</c> is not.
/// </para>
/// <para>
/// The assertions here drive a real admission chain from a real tool invocation rather than checking
/// which wrapper type sits on the rail. A type assertion would still pass if the wrapper stopped
/// enforcing, which is the failure mode this assembly's <c>CLAUDE.md</c> records shipping repeatedly.
/// </para>
/// </remarks>
public sealed class AgentExecutionContextFactoryBundleGovernanceTests : IDisposable
{
    private const string SkillName = "bundle-skill";

    /// <summary>The one tool the bundle's skill declares, so the agent is built holding something real.</summary>
    private const string AllowedTool = "file_system";

    /// <summary>
    /// The refusal a denying governor returns. Asserted on rather than merely "something happened",
    /// so a tool that ran for real and produced its own error text cannot be mistaken for a refusal.
    /// </summary>
    private const string DenialMessage = "Error: tool call refused by the admission chain.";

    private readonly SkillDirectoryFixture _skills = new("bundlegovernance");
    private readonly string _skillDir;
    private readonly string _skillsRoot;

    public AgentExecutionContextFactoryBundleGovernanceTests()
    {
        _skillDir = _skills.CreateSkill(
            Path.Combine("skills", SkillName),
            "# Bundle Skill\n\nBUNDLE_BODY",
            "A skill shipped inside an agent bundle.");
        _skillsRoot = Path.GetDirectoryName(_skillDir)!;
    }

    public void Dispose() => _skills.Dispose();

    /// <summary>
    /// A skill in the shape a staged bundle produces: parsed from a real directory on disk, so the
    /// framework provider will serve its body on demand and therefore publishes the disclosure tools.
    /// </summary>
    /// <remarks>
    /// It declares <c>run_skill_script</c> as well as its domain tool, and that is the whole point of the
    /// scenario rather than setup noise. A bundle declares what it <em>wants</em>; the caller's envelope
    /// is what it <em>gets</em>. Without the declaration, <see cref="ToolPermissionFilter"/> strips the
    /// script tool off the rail before governance is reached and the interesting case never arises —
    /// which is exactly what happened the first time this test was written.
    /// </remarks>
    private SkillDefinition MakeSkill() => new()
    {
        Id = SkillName,
        Name = SkillName,
        Description = "A skill shipped inside an agent bundle.",
        Instructions = "# Bundle Skill\n\nBUNDLE_BODY",
        BaseDirectory = _skillDir,
        AllowedTools = [AllowedTool, AgentSkillsProvider.RunSkillScriptToolName]
    };

    /// <remarks>
    /// Governance is left unconfigured, so the host's global <c>EnforceToolInvocation</c> switch is off.
    /// That is the default composition and the one that carried the defect — enforcement here comes from
    /// the ambient envelope alone, which is the whole point. Setting the switch would prove nothing
    /// either way: the factory reads it nowhere.
    /// </remarks>
    private AgentExecutionContextFactory CreateFactory()
    {
        var appConfig = new AppConfig
        {
            AI = new AIConfig
            {
                AgentFramework = new AgentFrameworkConfig { DefaultDeployment = "gpt-4o" },
                Skills = new SkillsConfig { BasePath = _skillsRoot }
            }
        };

        var services = new ServiceCollection();
        services.AddKeyedSingleton<ITool>(AllowedTool, (_, _) => new StubTool(AllowedTool));
        services.AddSingleton<IContentRedactionFilter>(TestRedactionFilter.Instance);
        var sp = services.BuildServiceProvider();

        return new AgentExecutionContextFactory(
            NullLogger<AgentExecutionContextFactory>.Instance,
            Mock.Of<IOptionsMonitor<AppConfig>>(m => m.CurrentValue == appConfig),
            sp,
            NullLoggerFactory.Instance,
            new ToolChainBuilder(
                NullLogger<ToolChainBuilder>.Instance, sp, TestRedactionFilter.Instance,
                new PassThroughToolConverter()),
            new SkillPrerequisiteResolver(),
            new UnsandboxedSkillFileReader());
    }

    /// <summary>
    /// Builds the agent, drives its rail the way the runtime does, and returns the named tool the model
    /// would finally be offered.
    /// </summary>
    private async Task<AIFunction> ResolveOfferedToolAsync(AgentExecutionContextFactory factory, string toolName)
    {
        var context = await factory.MapToAgentContextAsync([MakeSkill()], new SkillAgentOptions());
        var finished = await AIContextRailDriver.DriveAsync(context);

        var tool = finished.Tools?
            .OfType<AIFunction>()
            .SingleOrDefault(t => t.Name == toolName);

        // Control for every test in this class: the channel under test must actually carry the tool. If
        // progressive disclosure stopped publishing it, every assertion below would pass vacuously.
        tool.Should().NotBeNull(
            $"'{toolName}' exists only on the provider rail, so without it on the finished context there "
            + "is nothing here to govern or to fail to govern");

        return tool!;
    }

    /// <summary>
    /// Invokes <paramref name="tool"/> with an admission chain armed that refuses everything, and reports
    /// what came back as text.
    /// </summary>
    /// <remarks>
    /// A governed tool never reaches its inner implementation and returns the refusal. An ungoverned one
    /// runs for real against an empty argument set and either returns its own output or throws; both are
    /// reported here as ordinary text so the failure reads as a failed assertion about governance rather
    /// than as an unhandled error somewhere else.
    /// </remarks>
    private static async Task<string?> InvokeUnderRefusingChainAsync(AIFunction tool)
    {
        var governor = AdmissionHarness.DenyingGovernor(DenialMessage);

        using var armed = ToolAdmissionAccessor.Begin(AdmissionHarness.Pipeline(governor: governor.Object));

        try
        {
            var result = await tool.InvokeAsync(new AIFunctionArguments(), CancellationToken.None);
            return result?.ToString();
        }
        catch (Exception ex)
        {
            // The message matters as much as the type: it is what tells whoever reads the failure that
            // the tool got as far as validating its own arguments, which only an ungoverned tool does.
            return $"the tool executed and threw {ex.GetType().Name}: {ex.Message}";
        }
    }

    /// <summary>
    /// Publishes an ambient capability envelope exactly as <c>BundleRunExecutor</c> does around a whole
    /// bundle conversation, so the agent built inside it is built at the moment production builds it.
    /// </summary>
    /// <remarks>
    /// The grant deliberately lists only the skill's domain tool. That is what a real envelope looks
    /// like — an operator grants the tools a caller may invoke, never the framework's own plumbing —
    /// and it is the condition under which a governed transport tool would be refused.
    /// </remarks>
    private static IDisposable BeginBundleRun() =>
        CapabilityEnvelopeAccessor.Begin(new CapabilityEnvelope
        {
            AllowedTools = [AllowedTool],
            AutonomyCeiling = AutonomyLevel.Autonomous
        });

    /// <summary>
    /// The defect (#347). A bundle run arms enforcement through its capability envelope rather than the
    /// host's global switch, so a capability published on the provider rail has to be admitted through
    /// the chain on a default composition too — where before it was published unwrapped.
    /// </summary>
    [Fact]
    public async Task MapToAgentContext_BundleRun_SkillScriptToolReachesTheAdmissionChain()
    {
        using var bundleRun = BeginBundleRun();

        var tool = await ResolveOfferedToolAsync(
            CreateFactory(), AgentSkillsProvider.RunSkillScriptToolName);

        var result = await InvokeUnderRefusingChainAsync(tool);

        result.Should().Contain(DenialMessage,
            "running a skill's script is a capability, and a bundle runs an agent the host did not write. "
            + "Published unwrapped, it executes without the caller's envelope ever being asked");
    }

    /// <summary>
    /// The other half, and the reason the wrapper cannot simply cover everything on the rail: the two
    /// content-transport tools must stay ungoverned or a bundle cannot read the skills it shipped with.
    /// </summary>
    /// <remarks>
    /// The refusal asserted above is the control for these: it proves the armed chain is real and that
    /// this instrument can see a refusal, so "no refusal" here is a fact about the exemption rather than
    /// about a chain that was never armed. What the envelope does to an unexempt tool is pinned
    /// end-to-end against the real governor by
    /// <c>ToolInvocationGovernorEnvelopeTests.ResolverAllowsToolTheEnvelopeDoesNotGrant_IsStillDenied</c>
    /// — a bundle's envelope names domain tools, so a governed <c>read_skill_resource</c> would be
    /// refused there and the agent would get a refusal string where its own skill body belongs.
    /// </remarks>
    [Theory]
    [InlineData(AgentSkillsProvider.LoadSkillToolName)]
    [InlineData(AgentSkillsProvider.ReadSkillResourceToolName)]
    public async Task MapToAgentContext_BundleRun_SkillContentToolIsNotGated(string toolName)
    {
        using var bundleRun = BeginBundleRun();

        var tool = await ResolveOfferedToolAsync(CreateFactory(), toolName);

        var result = await InvokeUnderRefusingChainAsync(tool);

        result.Should().NotContain(DenialMessage,
            "these two carry the instructions for skills the agent was already assigned — they are not "
            + "capabilities a caller is granted, and the provider only ever advertises this agent's own "
            + "skills. Gating them asks a question no envelope answers, and the agent silently loses its "
            + "own skill bodies");

        result.Should().Contain("executed",
            "and it must be ungated because it RAN, not because the chain was never armed — the tool "
            + "reaching its own argument validation is what proves it got past the wrapper");
    }
}
