using Application.AI.Common.Interfaces;
using Application.AI.Common.Models.Conversations;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.Models.Conversations;

/// <summary>
/// Tests for <see cref="ToolCallReplayWindowPolicy"/> and the two
/// <see cref="ConversationMessageMapping"/> overloads built on it (#515) — the shared vocabulary that
/// replaced two hand-written, byte-identical <c>ToMeaiHistory</c> copies in <c>AgUiRunHandler</c> and
/// <c>ConversationOrchestrator</c>.
/// </summary>
public sealed class ToolCallReplayWindowPolicyTests
{
    [Fact]
    public void FromCurrentSettings_ReadsBothPropertiesFromTheTreatment()
    {
        var treatment = new Mock<IToolCallReplayTreatment>();
        treatment.Setup(t => t.Enabled).Returns(true);
        treatment.Setup(t => t.MaxReplayedChars).Returns(12345);

        var policy = ToolCallReplayWindowPolicy.FromCurrentSettings(treatment.Object);

        policy.ReplayToolCalls.Should().BeTrue();
        policy.MaxReplayedChars.Should().Be(12345);
    }

    [Fact]
    public void FromCurrentSettings_NullTreatment_Throws()
    {
        var act = () => ToolCallReplayWindowPolicy.FromCurrentSettings(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void FromCurrentSettings_CalledTwice_ReflectsWhateverTheMockReturnsAtEachCall()
    {
        // Proves "live" is a property of WHEN a caller calls this, not of the record itself — the same
        // factory, called again after the underlying setting changes, produces a different policy. This
        // is what AgUiRunHandler/ConversationOrchestrator rely on by calling it fresh every turn, and
        // what DurableTranscript deliberately does NOT do by calling it once and reusing the result.
        var treatment = new Mock<IToolCallReplayTreatment>();
        treatment.Setup(t => t.Enabled).Returns(true);
        treatment.SetupSequence(t => t.MaxReplayedChars).Returns(1000).Returns(2000);

        var first = ToolCallReplayWindowPolicy.FromCurrentSettings(treatment.Object);
        var second = ToolCallReplayWindowPolicy.FromCurrentSettings(treatment.Object);

        first.MaxReplayedChars.Should().Be(1000);
        second.MaxReplayedChars.Should().Be(2000);
    }

    [Fact]
    public void ToChatMessages_PolicyOverload_MatchesTheLooseParameterOverload()
    {
        var transcript = new List<ConversationMessage>
        {
            new(Guid.NewGuid(), MessageRole.User, "hello", DateTimeOffset.UtcNow),
            new(Guid.NewGuid(), MessageRole.Assistant, "hi there", DateTimeOffset.UtcNow),
        };
        var policy = new ToolCallReplayWindowPolicy(ReplayToolCalls: true, MaxReplayedChars: 500);

        var viaPolicy = ConversationMessageMapping.ToChatMessages(transcript, policy);
        var viaLooseParams = ConversationMessageMapping.ToChatMessages(
            transcript, policy.ReplayToolCalls, policy.MaxReplayedChars);

        viaPolicy.Should().HaveCount(viaLooseParams.Count);
        viaPolicy.Select(m => m.Role).Should().BeEquivalentTo(viaLooseParams.Select(m => m.Role));
    }

    [Fact]
    public void ToChatMessagesFromLiveSettings_DelegatesToThePolicyOverloadUsingCurrentTreatmentValues()
    {
        var toolCall = new ToolCallRecord(
            "search", """{"q":"weather"}""", """{"r":"sunny"}""", DurationMs: 1, CallId: "call-1", RoundOrdinal: 0);
        var transcript = new List<ConversationMessage>
        {
            new(Guid.NewGuid(), MessageRole.Assistant, "it's sunny", DateTimeOffset.UtcNow, ToolCalls: [toolCall]),
        };
        var treatment = new Mock<IToolCallReplayTreatment>();
        treatment.Setup(t => t.Enabled).Returns(true);
        treatment.Setup(t => t.MaxReplayedChars).Returns(int.MaxValue);

        var result = ConversationMessageMapping.ToChatMessagesFromLiveSettings(transcript, treatment.Object);

        // Expansion (call + result + trailing text = 3 messages) only happens when ReplayToolCalls is
        // true — proves the live Enabled value actually reached the projection, not just that some
        // projection ran.
        result.Should().HaveCount(3);
        result[0].Contents.OfType<FunctionCallContent>().Should().ContainSingle();
    }

    [Fact]
    public void ToChatMessagesFromLiveSettings_TreatmentDisabled_FallsBackToTextOnlyProjection()
    {
        var toolCall = new ToolCallRecord(
            "search", """{"q":"weather"}""", """{"r":"sunny"}""", DurationMs: 1, CallId: "call-1", RoundOrdinal: 0);
        var transcript = new List<ConversationMessage>
        {
            new(Guid.NewGuid(), MessageRole.Assistant, "it's sunny", DateTimeOffset.UtcNow, ToolCalls: [toolCall]),
        };
        var treatment = new Mock<IToolCallReplayTreatment>();
        treatment.Setup(t => t.Enabled).Returns(false);

        var result = ConversationMessageMapping.ToChatMessagesFromLiveSettings(transcript, treatment.Object);

        result.Should().ContainSingle();
        result[0].Contents.OfType<FunctionCallContent>().Should().BeEmpty();
    }
}
