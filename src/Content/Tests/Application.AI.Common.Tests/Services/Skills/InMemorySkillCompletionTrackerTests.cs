using Application.AI.Common.Services.Skills;
using Xunit;

namespace Application.AI.Common.Tests.Services.Skills;

public class InMemorySkillCompletionTrackerTests
{
    private readonly InMemorySkillCompletionTracker _tracker = new();

    [Fact]
    public void IsCompleted_ReturnsFalse_WhenNotMarked()
    {
        Assert.False(_tracker.IsCompleted("conv1", "validate"));
    }

    [Fact]
    public void MarkCompleted_ThenIsCompleted_ReturnsTrue()
    {
        _tracker.MarkCompleted("conv1", "validate");

        Assert.True(_tracker.IsCompleted("conv1", "validate"));
    }

    [Fact]
    public void IsCompleted_ScopedToConversation()
    {
        _tracker.MarkCompleted("conv1", "validate");

        Assert.False(_tracker.IsCompleted("conv2", "validate"));
    }

    [Fact]
    public void GetCompletedSkills_ReturnsAllCompleted()
    {
        _tracker.MarkCompleted("conv1", "validate");
        _tracker.MarkCompleted("conv1", "test");

        var completed = _tracker.GetCompletedSkills("conv1");

        Assert.Equal(2, completed.Count);
        Assert.Contains("validate", completed);
        Assert.Contains("test", completed);
    }

    [Fact]
    public void GetCompletedSkills_EmptyForUnknownConversation()
    {
        Assert.Empty(_tracker.GetCompletedSkills("unknown"));
    }

    [Fact]
    public void ClearConversation_RemovesState()
    {
        _tracker.MarkCompleted("conv1", "validate");

        _tracker.ClearConversation("conv1");

        Assert.False(_tracker.IsCompleted("conv1", "validate"));
        Assert.Empty(_tracker.GetCompletedSkills("conv1"));
    }

    [Fact]
    public void MarkCompleted_Idempotent()
    {
        _tracker.MarkCompleted("conv1", "validate");
        _tracker.MarkCompleted("conv1", "validate");

        Assert.Single(_tracker.GetCompletedSkills("conv1"));
    }

    [Fact]
    public void IsCompleted_CaseInsensitive()
    {
        _tracker.MarkCompleted("conv1", "Validate");

        Assert.True(_tracker.IsCompleted("conv1", "validate"));
        Assert.True(_tracker.IsCompleted("conv1", "VALIDATE"));
    }

    [Fact]
    public void ClearConversation_DoesNotAffectOtherConversations()
    {
        _tracker.MarkCompleted("conv1", "validate");
        _tracker.MarkCompleted("conv2", "deploy");

        _tracker.ClearConversation("conv1");

        Assert.True(_tracker.IsCompleted("conv2", "deploy"));
    }
}
