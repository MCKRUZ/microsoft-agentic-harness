using System.Text.Json;
using FluentAssertions;
using Presentation.AgentHub.AgUi;
using Xunit;

namespace Presentation.AgentHub.Tests.AgUi;

/// <summary>
/// Serialization tests for the Magentic workflow progress AG-UI events: the wire discriminator and the
/// property names a client keys on.
/// </summary>
public sealed class AgUiMagenticEventSerializationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private static string Serialize(AgUiEvent evt) => JsonSerializer.Serialize(evt, JsonOptions);

    [Fact]
    public void WorkflowStarted_CarriesTheParticipants()
    {
        var json = Serialize(new MagenticWorkflowStartedEvent
        {
            WorkflowId = "wf-1",
            WorkflowName = "research-team",
            Participants = ["researcher", "writer"],
        });

        json.Should().Contain("\"type\":\"MAGENTIC_WORKFLOW_STARTED\"");
        json.Should().Contain("\"workflowId\":\"wf-1\"");
        json.Should().Contain("\"workflowName\":\"research-team\"");
        json.Should().Contain("\"participants\":[\"researcher\",\"writer\"]");
    }

    [Fact]
    public void Plan_CarriesTheVersionAndText()
    {
        var json = Serialize(new MagenticPlanEvent { WorkflowId = "wf-1", PlanVersion = 2, PlanText = "step one" });

        json.Should().Contain("\"type\":\"MAGENTIC_PLAN\"");
        json.Should().Contain("\"planVersion\":2");
        json.Should().Contain("\"planText\":\"step one\"");
    }

    [Fact]
    public void Round_CarriesTheSpeakerInstructionAndJudgements_AndOmitsAbsentOnes()
    {
        var full = Serialize(new MagenticRoundEvent
        {
            WorkflowId = "wf-1",
            Round = 3,
            NextSpeaker = "researcher",
            Instruction = "find the report",
            RequestSatisfied = false,
            InLoop = true,
            Progressing = false,
        });
        var bare = Serialize(new MagenticRoundEvent
        {
            WorkflowId = "wf-1",
            Round = 4,
            RequestSatisfied = true,
            InLoop = false,
            Progressing = true,
        });

        full.Should().Contain("\"type\":\"MAGENTIC_ROUND\"");
        full.Should().Contain("\"round\":3");
        full.Should().Contain("\"nextSpeaker\":\"researcher\"");
        full.Should().Contain("\"instruction\":\"find the report\"");
        full.Should().Contain("\"inLoop\":true");
        full.Should().Contain("\"progressing\":false");
        bare.Should().NotContain("nextSpeaker").And.NotContain("instruction");
    }

    [Fact]
    public void PlanReviewRequested_CarriesThePlanAndWhetherItWasAStall()
    {
        var json = Serialize(new MagenticPlanReviewRequestedEvent
        {
            WorkflowId = "wf-1",
            PlanText = "draft",
            PlanTruncated = true,
            IsStalled = true,
        });

        json.Should().Contain("\"type\":\"MAGENTIC_PLAN_REVIEW_REQUESTED\"");
        json.Should().Contain("\"planText\":\"draft\"");
        json.Should().Contain("\"planTruncated\":true");
        json.Should().Contain("\"isStalled\":true");
    }

    [Fact]
    public void WorkflowCompleted_CarriesTheReasonAndRounds()
    {
        var json = Serialize(new MagenticWorkflowCompletedEvent
        {
            WorkflowId = "wf-1",
            CompletionReason = "satisfied",
            RoundsExecuted = 5,
        });

        json.Should().Contain("\"type\":\"MAGENTIC_WORKFLOW_COMPLETED\"");
        json.Should().Contain("\"completionReason\":\"satisfied\"");
        json.Should().Contain("\"roundsExecuted\":5");
    }

    [Fact]
    public void WorkflowFailed_CarriesAStableCode()
    {
        var json = Serialize(new MagenticWorkflowFailedEvent { WorkflowId = "wf-1", ErrorCode = "magentic.cancelled" });

        json.Should().Contain("\"type\":\"MAGENTIC_WORKFLOW_FAILED\"");
        json.Should().Contain("\"errorCode\":\"magentic.cancelled\"");
    }
}
