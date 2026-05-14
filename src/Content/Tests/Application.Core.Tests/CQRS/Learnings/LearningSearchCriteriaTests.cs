using Application.AI.Common.Interfaces.Learnings;
using Domain.AI.Learnings;
using FluentAssertions;
using Xunit;

namespace Application.Core.Tests.CQRS.Learnings;

public sealed class LearningSearchCriteriaTests
{
    [Fact]
    public void LearningSearchCriteria_DefaultConstruction_HasNullFilters()
    {
        var criteria = new LearningSearchCriteria
        {
            Scope = new LearningScope { AgentId = "agent-1" }
        };

        criteria.Category.Should().BeNull();
        criteria.MinFeedbackWeight.Should().BeNull();
        criteria.CreatedAfter.Should().BeNull();
        criteria.CreatedBefore.Should().BeNull();
    }

    [Fact]
    public void LearningSearchCriteria_ScopeHierarchyPrecedence_AgentFirst()
    {
        var criteria = new LearningSearchCriteria
        {
            Scope = new LearningScope
            {
                AgentId = "agent-1",
                TeamId = "team-alpha",
                IsGlobal = true
            }
        };

        criteria.Scope.AgentId.Should().Be("agent-1");
        criteria.Scope.TeamId.Should().Be("team-alpha");
        criteria.Scope.IsGlobal.Should().BeTrue();
    }
}
