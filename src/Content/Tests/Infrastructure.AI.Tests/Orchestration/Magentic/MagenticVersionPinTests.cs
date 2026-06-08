using System.Linq;
using FluentAssertions;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Specialized.Magentic;
using Xunit;

#pragma warning disable MAAIW001

namespace Infrastructure.AI.Tests.Orchestration.Magentic;

/// <summary>
/// PR-6 canary test for MAF Magentic API stability. Locks the public event
/// surface the harness instruments against so that a MAF package bump that
/// renames or removes a Magentic event type / property surfaces as a failing
/// test rather than a runtime surprise.
/// </summary>
/// <remarks>
/// Updating MAF: when this test fails, decide whether the rename is a
/// passthrough (update the test + the subscriber's event handlers) or a
/// breaking re-architecture (write a migration note in the PR, update the
/// span schema doc).
/// </remarks>
public sealed class MagenticVersionPinTests
{
    [Fact]
    public void MagenticOrchestratorEvent_derives_from_WorkflowEvent()
    {
        typeof(MagenticOrchestratorEvent).BaseType.Should().Be(typeof(WorkflowEvent));
    }

    [Fact]
    public void MagenticPlanCreatedEvent_has_FullTaskLedger_chat_message()
    {
        var prop = typeof(MagenticPlanCreatedEvent).GetProperty("FullTaskLedger");
        prop.Should().NotBeNull("the subscriber reads FullTaskLedger to record the plan event");
        prop!.PropertyType.Should().Be(typeof(Microsoft.Extensions.AI.ChatMessage));
    }

    [Fact]
    public void MagenticReplannedEvent_has_FullTaskLedger_chat_message()
    {
        var prop = typeof(MagenticReplannedEvent).GetProperty("FullTaskLedger");
        prop.Should().NotBeNull("the change-proposal router reads FullTaskLedger to classify the replan");
        prop!.PropertyType.Should().Be(typeof(Microsoft.Extensions.AI.ChatMessage));
    }

    [Fact]
    public void MagenticProgressLedgerUpdatedEvent_has_ProgressLedger()
    {
        var prop = typeof(MagenticProgressLedgerUpdatedEvent).GetProperty("ProgressLedger");
        prop.Should().NotBeNull();
        prop!.PropertyType.Should().Be(typeof(MagenticProgressLedger));
    }

    [Fact]
    public void MagenticPlanReviewRequest_has_required_properties()
    {
        var t = typeof(MagenticPlanReviewRequest);
        t.GetProperty("Plan").Should().NotBeNull();
        t.GetProperty("CurrentProgress").Should().NotBeNull();
        t.GetProperty("IsStalled").Should().NotBeNull();
        t.GetMethod("Approve").Should().NotBeNull();
    }

    [Fact]
    public void MagenticProgressLedger_has_six_boolean_and_string_properties()
    {
        var t = typeof(MagenticProgressLedger);
        t.GetProperty("IsStarted")!.PropertyType.Should().Be(typeof(bool));
        t.GetProperty("IsRequestSatisfied")!.PropertyType.Should().Be(typeof(bool));
        t.GetProperty("IsInLoop")!.PropertyType.Should().Be(typeof(bool));
        t.GetProperty("IsProgressBeingMade")!.PropertyType.Should().Be(typeof(bool));
        t.GetProperty("NextSpeaker")!.PropertyType.Should().Be(typeof(string));
        t.GetProperty("InstructionOrQuestion")!.PropertyType.Should().Be(typeof(string));
    }
}

#pragma warning restore MAAIW001
