using Application.AI.Common.Factories;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Governance;
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
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.Factories;

/// <summary>
/// Pins the security property that decides whether <see cref="GoverningToolContextProvider"/> is worth
/// having at all: a tool the model is offered through the <see cref="AIContextProvider"/> channel must
/// reach the admission chain, on <em>every</em> flow where governance is active — including a bundle run
/// with the host's global enforcement switch left off.
/// </summary>
/// <remarks>
/// <para>
/// Two channels put tools in front of the model. Most arrive through <c>ToolChainBuilder</c>, which wraps
/// them on the way past. The framework's own progressive-disclosure tools do not: they are contributed by
/// <see cref="AgentSkillsProvider"/> and exist only on the provider rail, so the wrapper this suite is
/// about is the only thing that can gate them. Two of the three — <c>load_skill</c> and
/// <c>read_skill_resource</c> — are additionally exempt from <see cref="ToolPermissionFilter"/> by design,
/// which leaves the admission chain as their sole gate rather than their second one.
/// </para>
/// <para>
/// A bundle run is exactly the flow that cannot afford to miss them: it executes an externally-authored
/// agent whose nested <c>SKILL.md</c> files were shipped by the bundle author, under a per-caller
/// capability envelope that says which tools that caller is allowed. Governance being active is therefore
/// not solely the host's global opt-in — the presence of an envelope arms it too
/// (<see cref="GovernanceEnforcement.IsActive"/>). Before issue #347 the rail read the global switch alone,
/// so on a default composition a bundle's disclosure tools were published unwrapped and the envelope was
/// never consulted for them.
/// </para>
/// <para>
/// The assertions here drive a real admission chain from a real tool invocation rather than checking that
/// the wrapper type is present on the rail. A type assertion would still pass if the wrapper stopped
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
    private SkillDefinition MakeSkill() => new()
    {
        Id = SkillName,
        Name = SkillName,
        Description = "A skill shipped inside an agent bundle.",
        Instructions = "# Bundle Skill\n\nBUNDLE_BODY",
        BaseDirectory = _skillDir,
        AllowedTools = [AllowedTool]
    };

    private AgentExecutionContextFactory CreateFactory(bool globalEnforcement)
    {
        var appConfig = new AppConfig
        {
            AI = new AIConfig
            {
                AgentFramework = new AgentFrameworkConfig { DefaultDeployment = "gpt-4o" },
                Skills = new SkillsConfig { BasePath = _skillsRoot },
                Governance = new GovernanceConfig { EnforceToolInvocation = globalEnforcement }
            }
        };

        var services = new ServiceCollection();
        services.AddKeyedSingleton<ITool>(AllowedTool, (_, _) => new StubTool(AllowedTool));
        var sp = services.BuildServiceProvider();

        return new AgentExecutionContextFactory(
            NullLogger<AgentExecutionContextFactory>.Instance,
            Mock.Of<IOptionsMonitor<AppConfig>>(m => m.CurrentValue == appConfig),
            sp,
            NullLoggerFactory.Instance,
            new ToolChainBuilder(NullLogger<ToolChainBuilder>.Instance, sp, new PassThroughToolConverter()),
            new SkillPrerequisiteResolver(),
            new UnsandboxedSkillFileReader());
    }

    /// <summary>
    /// Builds the agent, drives its rail the way the runtime does, and returns the disclosure tool the
    /// model would finally be offered.
    /// </summary>
    private async Task<AIFunction> ResolveDisclosureToolAsync(AgentExecutionContextFactory factory)
    {
        var context = await factory.MapToAgentContextAsync([MakeSkill()], new SkillAgentOptions());
        var finished = await AIContextRailDriver.DriveAsync(context);

        var tool = finished.Tools?
            .OfType<AIFunction>()
            .SingleOrDefault(t => t.Name == AgentSkillsProvider.ReadSkillResourceToolName);

        // Control for every test in this class: the channel under test must actually carry a tool. If
        // progressive disclosure stopped publishing one, every assertion below would pass vacuously.
        tool.Should().NotBeNull(
            "the framework's skill-disclosure tools are the tools that exist only on the provider rail; "
            + "without one on the finished context there is nothing here to govern or to fail to govern");

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
        var governor = new Mock<IToolInvocationGovernor>();
        governor
            .Setup(g => g.AuthorizeAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<IReadOnlyDictionary<string, object?>?>()))
            .ReturnsAsync(ToolInvocationDecision.Deny(DenialMessage));

        using var armed = ToolAdmissionAccessor.Begin(AdmissionHarness.Pipeline(governor: governor.Object));

        try
        {
            var result = await tool.InvokeAsync(new AIFunctionArguments(), CancellationToken.None);
            return result?.ToString();
        }
        catch (Exception ex)
        {
            return $"the tool executed and threw {ex.GetType().Name}";
        }
    }

    /// <summary>
    /// The control. With the host's global switch on, the disclosure tool is governed — which is what
    /// makes the refusal below a usable instrument for the case that follows.
    /// </summary>
    [Fact]
    public async Task MapToAgentContext_GlobalEnforcementOn_DisclosureToolReachesTheAdmissionChain()
    {
        var tool = await ResolveDisclosureToolAsync(CreateFactory(globalEnforcement: true));

        var result = await InvokeUnderRefusingChainAsync(tool);

        result.Should().Contain(DenialMessage,
            "with enforcement on, a tool contributed through the provider channel must still be admitted "
            + "through the chain — if this refusal is not observable here, the assertion below proves nothing");
    }

    /// <summary>
    /// The defect (#347). A bundle run arms enforcement through its capability envelope, not through the
    /// host's global switch, so the disclosure tools must be governed on a default composition too.
    /// </summary>
    [Fact]
    public async Task MapToAgentContext_BundleRunWithGlobalEnforcementOff_DisclosureToolReachesTheAdmissionChain()
    {
        // The bundle-run fact, published exactly as BundleRunExecutor publishes it around the whole
        // conversation — so the agent below is constructed inside the envelope, as it is in production.
        var envelope = new CapabilityEnvelope
        {
            AllowedTools = [AllowedTool],
            AutonomyCeiling = AutonomyLevel.Autonomous
        };

        using var bundleRun = CapabilityEnvelopeAccessor.Begin(envelope);

        var tool = await ResolveDisclosureToolAsync(CreateFactory(globalEnforcement: false));

        var result = await InvokeUnderRefusingChainAsync(tool);

        result.Should().Contain(DenialMessage,
            "a bundle runs an agent the host did not write, under a per-caller grant; read_skill_resource "
            + "is exempt from the tool allowlist by design, so the admission chain is its only gate. A "
            + "disclosure tool published unwrapped here is a capability the envelope was never asked about");
    }
}
