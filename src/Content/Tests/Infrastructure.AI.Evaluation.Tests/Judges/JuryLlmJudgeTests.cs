using Application.AI.Common.Evaluation.Interfaces;
using Application.AI.Common.Evaluation.Models;
using Application.AI.Common.Evaluation.Outcomes;
using Domain.AI.Evaluation;
using Domain.Common.Config.AI;
using FluentAssertions;
using Infrastructure.AI.Evaluation.Judges;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.Evaluation.Tests.Judges;

public sealed class JuryLlmJudgeTests
{
    private static Mock<IChatClient> ClientReturning(params string[] responses)
    {
        var queue = new Queue<string>(responses);
        var client = new Mock<IChatClient>();
        client.Setup(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                var text = queue.Count > 0 ? queue.Dequeue() : "{}";
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, text))
                {
                    Usage = new UsageDetails { InputTokenCount = 10, OutputTokenCount = 5 }
                };
            });
        return client;
    }

    private static IOptionsMonitor<T> Monitor<T>(T value)
    {
        var mon = new Mock<IOptionsMonitor<T>>();
        mon.SetupGet(m => m.CurrentValue).Returns(value);
        return mon.Object;
    }

    private static string Score(double s) => $$"""{"score": {{s}}, "reasoning": "ok"}""";

    private static JuryPanelistSpec Panelist(string name, string deployment) => new()
    {
        Name = name,
        ClientType = AIAgentFrameworkClientType.AzureOpenAI,
        Deployment = deployment
    };

    private static LlmJudgeRequest Request() => new()
    {
        SystemPromptCore = "You are a judge.",
        UserPromptTemplate = "Score this: {{x}}",
        Variables = new Dictionary<string, string?> { ["x"] = "answer" }
    };

    private static JuryLlmJudge MakeSut(Mock<IJudgeChatClientProvider> provider, JuryOptions jury)
    {
        var single = new DefaultLlmJudge(provider.Object, NullLogger<DefaultLlmJudge>.Instance);
        return new JuryLlmJudge(
            single,
            provider.Object,
            Monitor(jury),
            Monitor(new JudgeOptions { Deployment = "default-model" }),
            NullLogger<JuryLlmJudge>.Instance);
    }

    [Fact]
    public async Task No_panel_delegates_to_single_judge_with_no_panel_metadata()
    {
        var provider = new Mock<IJudgeChatClientProvider>();
        provider.Setup(p => p.GetJudgeAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(ClientReturning(Score(0.7)).Object);

        var sut = MakeSut(provider, new JuryOptions()); // empty panel

        var result = await sut.JudgeAsync(Request(), CancellationToken.None);

        result.Outcome.Should().Be(LlmJudgeOutcome.Parsed);
        result.Score.Should().Be(0.7);
        result.Panel.Should().BeNull("an unconfigured panel must behave exactly like a single judge");
    }

    [Fact]
    public async Task Agreeing_panel_aggregates_median_and_buckets_consensus()
    {
        var provider = new Mock<IJudgeChatClientProvider>();
        provider.Setup(p => p.GetJudgeAsync(It.IsAny<AIAgentFrameworkClientType>(), "a", It.IsAny<CancellationToken>()))
            .ReturnsAsync(ClientReturning(Score(0.80)).Object);
        provider.Setup(p => p.GetJudgeAsync(It.IsAny<AIAgentFrameworkClientType>(), "b", It.IsAny<CancellationToken>()))
            .ReturnsAsync(ClientReturning(Score(0.85)).Object);
        provider.Setup(p => p.GetJudgeAsync(It.IsAny<AIAgentFrameworkClientType>(), "c", It.IsAny<CancellationToken>()))
            .ReturnsAsync(ClientReturning(Score(0.90)).Object);

        var jury = new JuryOptions
        {
            Panelists = { Panelist("A", "a"), Panelist("B", "b"), Panelist("C", "c") }
        };
        var sut = MakeSut(provider, jury);

        var result = await sut.JudgeAsync(Request(), CancellationToken.None);

        result.Outcome.Should().Be(LlmJudgeOutcome.Parsed);
        result.Score.Should().Be(0.85); // median
        result.Panel.Should().NotBeNull();
        result.Panel!.Bucket.Should().Be(ConsensusBucket.Consensus);
        result.Panel.Responded.Should().Be(3);
        result.Panel.Excluded.Should().Be(0);
    }

    [Fact]
    public async Task Median_absorbs_one_outlier_panelist_but_flags_conflict()
    {
        var provider = new Mock<IJudgeChatClientProvider>();
        provider.Setup(p => p.GetJudgeAsync(It.IsAny<AIAgentFrameworkClientType>(), "a", It.IsAny<CancellationToken>()))
            .ReturnsAsync(ClientReturning(Score(0.80)).Object);
        provider.Setup(p => p.GetJudgeAsync(It.IsAny<AIAgentFrameworkClientType>(), "b", It.IsAny<CancellationToken>()))
            .ReturnsAsync(ClientReturning(Score(0.85)).Object);
        provider.Setup(p => p.GetJudgeAsync(It.IsAny<AIAgentFrameworkClientType>(), "c", It.IsAny<CancellationToken>()))
            .ReturnsAsync(ClientReturning(Score(0.0)).Object);

        var jury = new JuryOptions
        {
            Panelists = { Panelist("A", "a"), Panelist("B", "b"), Panelist("C", "c") }
        };
        var sut = MakeSut(provider, jury);

        var result = await sut.JudgeAsync(Request(), CancellationToken.None);

        result.Score.Should().Be(0.80); // median ignores the 0.0 outlier
        result.Panel!.Bucket.Should().Be(ConsensusBucket.Conflict); // but the disagreement is surfaced
    }

    [Fact]
    public async Task Malformed_panelist_is_excluded_and_the_rest_aggregate()
    {
        var provider = new Mock<IJudgeChatClientProvider>();
        provider.Setup(p => p.GetJudgeAsync(It.IsAny<AIAgentFrameworkClientType>(), "a", It.IsAny<CancellationToken>()))
            .ReturnsAsync(ClientReturning(Score(0.60)).Object);
        provider.Setup(p => p.GetJudgeAsync(It.IsAny<AIAgentFrameworkClientType>(), "b", It.IsAny<CancellationToken>()))
            .ReturnsAsync(ClientReturning(Score(0.60)).Object);
        provider.Setup(p => p.GetJudgeAsync(It.IsAny<AIAgentFrameworkClientType>(), "c", It.IsAny<CancellationToken>()))
            .ReturnsAsync(ClientReturning("not json", "still not json").Object);

        var jury = new JuryOptions
        {
            Panelists = { Panelist("A", "a"), Panelist("B", "b"), Panelist("C", "c") }
        };
        var sut = MakeSut(provider, jury);

        var result = await sut.JudgeAsync(Request(), CancellationToken.None);

        result.Outcome.Should().Be(LlmJudgeOutcome.Parsed);
        result.Score.Should().Be(0.60);
        result.Panel!.Responded.Should().Be(2);
        result.Panel.Excluded.Should().Be(1);
        result.Panel.Verdicts.Should().HaveCount(3);
    }

    [Fact]
    public async Task All_panelists_failing_preserves_the_soft_fail_contract()
    {
        var provider = new Mock<IJudgeChatClientProvider>();
        // Distinct client per panelist — each with its own response queue — so a drained
        // shared queue can't fall back to a parseable default and mask the failure.
        provider.Setup(p => p.GetJudgeAsync(It.IsAny<AIAgentFrameworkClientType>(), "a", It.IsAny<CancellationToken>()))
            .ReturnsAsync(ClientReturning("garbage", "garbage").Object);
        provider.Setup(p => p.GetJudgeAsync(It.IsAny<AIAgentFrameworkClientType>(), "b", It.IsAny<CancellationToken>()))
            .ReturnsAsync(ClientReturning("garbage", "garbage").Object);

        var jury = new JuryOptions
        {
            Panelists = { Panelist("A", "a"), Panelist("B", "b") }
        };
        var sut = MakeSut(provider, jury);

        var result = await sut.JudgeAsync(Request(), CancellationToken.None);

        result.Outcome.Should().Be(LlmJudgeOutcome.Malformed);
        result.Score.Should().Be(0.0);
        result.Panel!.Responded.Should().Be(0);
    }

    [Fact]
    public async Task Panel_cost_is_summed_across_panelists()
    {
        var provider = new Mock<IJudgeChatClientProvider>();
        provider.Setup(p => p.GetJudgeAsync(It.IsAny<AIAgentFrameworkClientType>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ClientReturning(Score(0.5)).Object);

        var jury = new JuryOptions
        {
            Panelists = { Panelist("A", "a"), Panelist("B", "b"), Panelist("C", "c") }
        };
        var sut = MakeSut(provider, jury);

        var result = await sut.JudgeAsync(Request(), CancellationToken.None);

        // 3 panelists × 10 input + 5 output tokens each.
        result.InputTokens.Should().Be(30);
        result.OutputTokens.Should().Be(15);
    }
}
