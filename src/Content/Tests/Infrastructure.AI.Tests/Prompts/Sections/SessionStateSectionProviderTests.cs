using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Context;
using Domain.AI.Prompts;
using FluentAssertions;
using Infrastructure.AI.Prompts.Sections;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Prompts.Sections;

/// <summary>
/// Tests for <see cref="SessionStateSectionProvider"/> covering turn number display,
/// budget breakdown rendering, and null/empty edge cases.
/// </summary>
public sealed class SessionStateSectionProviderTests
{
    private readonly Mock<IAgentExecutionContext> _context = new();
    private readonly Mock<IContextBudgetTracker> _budget = new();

    [Fact]
    public void SectionType_IsSessionState()
    {
        var provider = new SessionStateSectionProvider(_context.Object, _budget.Object);

        provider.SectionType.Should().Be(SystemPromptSectionType.SessionState);
    }

    [Fact]
    public async Task GetSectionAsync_NoDataAvailable_ReturnsNull()
    {
        _context.Setup(c => c.TurnNumber).Returns((int?)null);
        _budget.Setup(b => b.GetTotalAllocated("agent-1")).Returns(0);

        var provider = new SessionStateSectionProvider(_context.Object, _budget.Object);

        var section = await provider.GetSectionAsync("agent-1");

        section.Should().BeNull();
    }

    [Fact]
    public async Task GetSectionAsync_WithTurnNumber_IncludesTurn()
    {
        _context.Setup(c => c.TurnNumber).Returns(5);
        _budget.Setup(b => b.GetTotalAllocated("agent-1")).Returns(0);

        var provider = new SessionStateSectionProvider(_context.Object, _budget.Object);

        var section = await provider.GetSectionAsync("agent-1");

        section.Should().NotBeNull();
        section!.Content.Should().Contain("Current turn: 5");
    }

    [Fact]
    public async Task GetSectionAsync_WithBudget_IncludesTokenAllocation()
    {
        _context.Setup(c => c.TurnNumber).Returns((int?)null);
        _budget.Setup(b => b.GetTotalAllocated("agent-1")).Returns(5000);
        _budget.Setup(b => b.GetBreakdown("agent-1")).Returns(
            new Dictionary<string, int>());

        var provider = new SessionStateSectionProvider(_context.Object, _budget.Object);

        var section = await provider.GetSectionAsync("agent-1");

        section.Should().NotBeNull();
        section!.Content.Should().Contain("Tokens allocated: 5,000");
    }

    [Fact]
    public async Task GetSectionAsync_WithBreakdown_IncludesComponentDetails()
    {
        _context.Setup(c => c.TurnNumber).Returns(1);
        _budget.Setup(b => b.GetTotalAllocated("agent-1")).Returns(10000);
        _budget.Setup(b => b.GetBreakdown("agent-1")).Returns(
            new Dictionary<string, int>
            {
                ["system_prompt"] = 3000,
                ["tool_schemas"] = 7000
            });

        var provider = new SessionStateSectionProvider(_context.Object, _budget.Object);

        var section = await provider.GetSectionAsync("agent-1");

        section.Should().NotBeNull();
        section!.Content.Should().Contain("system_prompt: 3,000 tokens");
        section.Content.Should().Contain("tool_schemas: 7,000 tokens");
        section.Content.Should().Contain("Budget breakdown:");
    }

    [Fact]
    public async Task GetSectionAsync_IsNotCacheable()
    {
        _context.Setup(c => c.TurnNumber).Returns(1);
        _budget.Setup(b => b.GetTotalAllocated("agent-1")).Returns(0);

        var provider = new SessionStateSectionProvider(_context.Object, _budget.Object);

        var section = await provider.GetSectionAsync("agent-1");

        section.Should().NotBeNull();
        section!.IsCacheable.Should().BeFalse();
    }

    [Fact]
    public async Task GetSectionAsync_Priority_Is50()
    {
        _context.Setup(c => c.TurnNumber).Returns(1);
        _budget.Setup(b => b.GetTotalAllocated("agent-1")).Returns(0);

        var provider = new SessionStateSectionProvider(_context.Object, _budget.Object);

        var section = await provider.GetSectionAsync("agent-1");

        section!.Priority.Should().Be(50);
    }

    [Fact]
    public async Task GetSectionAsync_EstimatedTokens_IsPositive()
    {
        _context.Setup(c => c.TurnNumber).Returns(3);
        _budget.Setup(b => b.GetTotalAllocated("agent-1")).Returns(1000);
        _budget.Setup(b => b.GetBreakdown("agent-1")).Returns(
            new Dictionary<string, int> { ["prompt"] = 1000 });

        var provider = new SessionStateSectionProvider(_context.Object, _budget.Object);

        var section = await provider.GetSectionAsync("agent-1");

        section!.EstimatedTokens.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Constructor_NullContext_Throws()
    {
        var act = () => new SessionStateSectionProvider(null!, _budget.Object);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullBudgetTracker_Throws()
    {
        var act = () => new SessionStateSectionProvider(_context.Object, null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
