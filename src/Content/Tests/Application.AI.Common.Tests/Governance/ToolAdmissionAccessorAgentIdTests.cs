using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Services.Governance;
using FluentAssertions;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.Governance;

/// <summary>
/// The accessor carries the agent a published pipeline belongs to (#772). A nested run (a delegation, a
/// Magentic participant) publishes its own pipeline; the agent id travels with it so a tool reading
/// "who is calling" gets the nested run's agent, not the enclosing turn's.
/// </summary>
public sealed class ToolAdmissionAccessorAgentIdTests
{
    private static IToolCallAdmissionPipeline Pipeline() => Mock.Of<IToolCallAdmissionPipeline>();

    [Fact]
    public void CurrentAgentId_OutsideAnyScope_IsNull()
    {
        ToolAdmissionAccessor.CurrentAgentId.Should().BeNull();
    }

    [Fact]
    public void Begin_WithAnAgentId_PublishesItAlongsideThePipeline_AndRestoresBothOnDispose()
    {
        var pipeline = Pipeline();

        using (ToolAdmissionAccessor.Begin(pipeline, "delegate-b"))
        {
            ToolAdmissionAccessor.Current.Should().BeSameAs(pipeline);
            ToolAdmissionAccessor.CurrentAgentId.Should().Be("delegate-b");
        }

        ToolAdmissionAccessor.Current.Should().BeNull();
        ToolAdmissionAccessor.CurrentAgentId.Should().BeNull();
    }

    [Fact]
    public void Begin_WithoutAnAgentId_DoesNotInheritTheEnclosingScopesAgentId()
    {
        // A pipeline published with no agent (the turn handlers) is not the enclosing delegate's pipeline;
        // letting the delegate's id show through would attribute this pipeline's calls to the wrong agent.
        using (ToolAdmissionAccessor.Begin(Pipeline(), "outer-agent"))
        using (ToolAdmissionAccessor.Begin(Pipeline()))
        {
            ToolAdmissionAccessor.CurrentAgentId.Should().BeNull();
        }
    }

    [Fact]
    public void DisposingAnInnerScope_RestoresTheEnclosingAgentId()
    {
        using (ToolAdmissionAccessor.Begin(Pipeline(), "outer-agent"))
        {
            using (ToolAdmissionAccessor.Begin(Pipeline(), "inner-agent"))
                ToolAdmissionAccessor.CurrentAgentId.Should().Be("inner-agent");

            ToolAdmissionAccessor.CurrentAgentId.Should().Be("outer-agent");
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Begin_RejectsABlankAgentId(string agentId)
    {
        var act = () => ToolAdmissionAccessor.Begin(Pipeline(), agentId);

        act.Should().Throw<ArgumentException>();
    }
}
