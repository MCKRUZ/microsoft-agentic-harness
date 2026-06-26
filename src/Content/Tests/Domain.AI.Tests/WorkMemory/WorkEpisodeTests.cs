using Domain.AI.WorkMemory;
using FluentAssertions;
using Xunit;

namespace Domain.AI.Tests.WorkMemory;

public sealed class WorkEpisodeTests
{
    [Fact]
    public void TotalTokens_IsSumOfInputAndOutput()
    {
        var episode = new WorkEpisode
        {
            EpisodeId = Guid.NewGuid(),
            AgentId = "agent-1",
            ConversationId = "conv-1",
            TurnNumber = 1,
            UserMessage = "task",
            ResponseSummary = "result",
            Outcome = EpisodeOutcome.Success,
            InputTokens = 100,
            OutputTokens = 25,
            CreatedAt = DateTimeOffset.UtcNow
        };

        episode.TotalTokens.Should().Be(125);
    }
}
