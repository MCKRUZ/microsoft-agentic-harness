using Application.AI.Common.Helpers;
using Application.AI.Common.Services.Context;
using Application.AI.Common.Services.Skills;
using Domain.Common.Config;
using FluentAssertions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.Services.Skills;

/// <summary>
/// Tests <see cref="BudgetChargingSkill"/>, which makes the framework's on-demand skill pulls visible to
/// the context budget.
/// </summary>
/// <remarks>
/// The failure this guards against is silent in both directions. Charge nothing and the budget under-reports
/// on the turns that load the most, which is the opposite of when a governance number needs to be right.
/// Charge and alter what comes back, and the model receives content the harness edited on its way through.
/// Every test below therefore asserts the charge <em>and</em> that the payload is unchanged.
/// </remarks>
public sealed class BudgetChargingSkillTests
{
    private const string AgentName = "DemoAgent";
    private const string SkillName = "demo-skill";
    private const string SkillBody = "# Demo\n\nDo the demo thing, carefully and at some length.";

    private readonly ContextBudgetTracker _tracker = new(
        Mock.Of<IOptionsMonitor<AppConfig>>(m => m.CurrentValue == new AppConfig()),
        NullLogger<ContextBudgetTracker>.Instance);

    private static AgentInlineSkill Inline(string body = SkillBody) =>
        new(SkillName, "A demo skill.", body);

    private BudgetChargingSkill Wrap(AgentSkill inner) => new(inner, AgentName, _tracker);

    private int Charged(string component) =>
        _tracker.GetBreakdown(AgentName).TryGetValue(component, out var tokens) ? tokens : 0;

    // ── Tier 2: the skill body served by load_skill ───────────────────────────

    [Fact]
    public async Task GetContent_BodyServed_ChargesItsMeasuredTokens()
    {
        var skill = Wrap(Inline());

        var content = await skill.GetContentAsync();

        Charged(BudgetChargingSkill.Tier2Component).Should().Be(
            TokenEstimationHelper.EstimateTokens(content),
            "the charge has to be measured from what actually reached the model — a fixed or estimated " +
            "figure would drift from the real cost the moment a skill body changed size");
    }

    [Fact]
    public async Task GetContent_BodyServed_ReturnsItUnchanged()
    {
        var inner = Inline();
        var expected = await inner.GetContentAsync();

        var actual = await Wrap(Inline()).GetContentAsync();

        actual.Should().Be(
            expected,
            "accounting must be transparent to the model; a wrapper that edits the body would change what " +
            "the agent was instructed to do in order to count it");
    }

    [Fact]
    public async Task GetContent_CalledTwice_ChargesTwice()
    {
        var skill = Wrap(Inline());

        var first = await skill.GetContentAsync();
        await skill.GetContentAsync();

        Charged(BudgetChargingSkill.Tier2Component).Should().Be(
            TokenEstimationHelper.EstimateTokens(first) * 2,
            "each load_skill call appends its own tool-result message, so a skill loaded twice really does " +
            "occupy the context twice — deduplicating would reinstate the under-reporting this exists to fix");
    }

    [Fact]
    public async Task GetContent_EmptyBody_ChargesNothing()
    {
        // A skill whose content pull yields nothing costs the model nothing.
        var skill = Wrap(new EmptyContentSkill(Inline().Frontmatter));

        var content = await skill.GetContentAsync();

        content.Should().BeEmpty();
        _tracker.GetTotalAllocated(AgentName).Should().Be(0);
    }

    // ── Tier 3: supporting files served by read_skill_resource ────────────────

    [Fact]
    public async Task ReadResource_FileServed_ChargesItsMeasuredTokens()
    {
        const string fileContent = "reference material the model asked for";
        var inline = Inline();
        inline.AddResource("references/guide.md", () => Task.FromResult(fileContent));

        var resource = await Wrap(inline).GetResourceAsync("references/guide.md");
        var value = await resource!.ReadAsync(new ServiceCollection().BuildServiceProvider());

        value?.ToString().Should().Be(fileContent, "the file must reach the model unedited");
        Charged(BudgetChargingSkill.Tier3Component).Should().Be(
            TokenEstimationHelper.EstimateTokens(fileContent),
            "Tier 3 is where the bulk lives — a budget blind to it is blind to the largest on-demand cost");
    }

    [Fact]
    public async Task GetResource_WithoutReading_ChargesNothing()
    {
        var inline = Inline();
        inline.AddResource("references/guide.md", () => Task.FromResult("never read"));

        var resource = await Wrap(inline).GetResourceAsync("references/guide.md");

        resource.Should().NotBeNull();
        _tracker.GetTotalAllocated(AgentName).Should().Be(
            0,
            "resolving a resource returns a descriptor, not content; billing here would charge for a file " +
            "the model may never receive and would miss the size of the one it does");
    }

    [Fact]
    public async Task GetResource_UnknownName_IsAMissAndCostsNothing()
    {
        var resource = await Wrap(Inline()).GetResourceAsync("references/absent.md");

        resource.Should().BeNull("a miss must stay a miss — inventing a descriptor would make the framework " +
            "report a file that does not exist");
        _tracker.GetTotalAllocated(AgentName).Should().Be(0);
    }

    [Fact]
    public async Task GetResource_KnownName_PreservesTheDescriptorIdentity()
    {
        var inline = Inline();
        inline.AddResource("references/guide.md", () => Task.FromResult("content"));

        var inner = await inline.GetResourceAsync("references/guide.md");
        var wrapped = await Wrap(inline).GetResourceAsync("references/guide.md");

        wrapped!.Name.Should().Be(
            inner!.Name,
            "the framework resolves and reports resources by name; a wrapper that renamed one would make it " +
            "unreachable through the very tool that just found it");
        wrapped.Description.Should().Be(inner.Description);
    }

    // ── Forwarding: everything this wrapper does not account for ──────────────

    [Fact]
    public void Frontmatter_AnyWrappedSkill_IsForwardedUnchanged()
    {
        var inline = Inline();

        var skill = Wrap(inline);

        skill.Frontmatter.Name.Should().Be(inline.Frontmatter.Name);
        skill.Frontmatter.Description.Should().Be(inline.Frontmatter.Description);
    }

    [Fact]
    public async Task GetScript_NoScriptRegistered_IsForwardedAndNotCharged()
    {
        // Skill scripts run through the harness's own sandboxed tool chain, so their output is accounted
        // for there. This call returns the definition, not the output — there is nothing here to charge.
        var skill = Wrap(Inline());

        var script = await skill.GetScriptAsync("anything");

        script.Should().BeNull("no scripts are registered on the production path");
        _tracker.GetTotalAllocated(AgentName).Should().Be(0);
    }

    [Fact]
    public void Construction_BlankAgentName_Throws()
    {
        // The budget is keyed by agent name. A blank one would file the tokens under a budget nothing reads,
        // which looks identical to not charging at all.
        var act = () => new BudgetChargingSkill(Inline(), "  ", _tracker);

        act.Should().Throw<ArgumentException>();
    }

    /// <summary>
    /// A skill whose content pull yields nothing, for proving an empty pull is not charged.
    /// </summary>
    private sealed class EmptyContentSkill(AgentSkillFrontmatter frontmatter) : AgentSkill
    {
        public override AgentSkillFrontmatter Frontmatter { get; } = frontmatter;

        public override ValueTask<string> GetContentAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(string.Empty);
    }
}
