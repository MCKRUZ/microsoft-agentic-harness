using Domain.AI.Planner;
using Domain.Common.Compliance;
using Domain.Common.Helpers;
using FluentAssertions;
using Xunit;

namespace Domain.AI.Tests.Compliance;

/// <summary>
/// Reserved conversation-id shapes (#907). Stores keyed by conversation id carry no owner, so erasing a person erases
/// whatever those stores hold under the person's conversation ids. Every id a planned-task run derives must therefore be
/// reserved, and a person's own conversation with a reserved id must not be followed into those stores.
/// </summary>
public sealed class ErasureSubjectReservedIdTests
{
    [Fact]
    public void EveryKeyAPlanRunDerives_IsReserved()
    {
        // If either key ever stops containing the separator, the conversation stores would accept it as an id a person
        // can open, and erasing that person would erase the run's records.
        ReservedConversationIds.IsReserved(PlanRunKeys.StepConversationId("plan-1", new PlanStepId(Guid.NewGuid()))).Should().BeTrue();
        ReservedConversationIds.IsReserved(PlanRunKeys.RunBudgetKey("plan-1")).Should().BeTrue();
    }

    [Fact]
    public void AllConversationIds_LeavesOutTheSubjectsReservedConversations_ButKeepsTheirStepIds()
    {
        var subject = new ErasureSubject(
            "user-1",
            ["conv-1", "run-9:step-1"],
            new PlanRunTraces(["plan-1:step-1"], ["job-1"], ["planrun:plan-1"]));

        subject.AllConversationIds.Should().BeEquivalentTo(["conv-1", "plan-1:step-1"],
            "a step id comes from the plan store's own record of the subject's run, so it is theirs");
        subject.WithheldConversationIds.Should().Equal("run-9:step-1");
        subject.ConversationIds.Should().Equal(["conv-1", "run-9:step-1"],
            "the conversation stores still erase the subject's own conversation, matched on the owner");
    }

    [Fact]
    public void AReservedConversation_IsWithheld_EvenWhenTheSubjectHasNoPlannedTasks()
    {
        // The common case: a person with no planned tasks at all. Their reserved id must still not reach the stores
        // without an owner.
        var subject = new ErasureSubject("user-1", ["conv-1", "run-9:step-1"]);

        subject.AllConversationIds.Should().Equal("conv-1");
        subject.WithheldConversationIds.Should().Equal("run-9:step-1");
    }

    [Fact]
    public void NoReservedConversation_WithholdsNothing()
    {
        var subject = new ErasureSubject("user-1", ["conv-1", "conv-2"]);

        subject.WithheldConversationIds.Should().BeEmpty();
        subject.AllConversationIds.Should().Equal("conv-1", "conv-2");
    }
}
