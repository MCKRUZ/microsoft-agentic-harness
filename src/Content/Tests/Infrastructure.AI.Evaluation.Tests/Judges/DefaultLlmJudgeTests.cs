using Application.AI.Common.Evaluation.Interfaces;
using Application.AI.Common.Evaluation.Models;
using Application.AI.Common.Evaluation.Outcomes;
using FluentAssertions;
using Infrastructure.AI.Evaluation.Judges;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.Evaluation.Tests.Judges;

public sealed class DefaultLlmJudgeTests
{
    private static (Mock<IJudgeChatClientProvider> provider, Mock<IChatClient> client) Plumbing(params string[] responses)
    {
        var clientMock = new Mock<IChatClient>();
        var queue = new Queue<string>(responses);
        clientMock.Setup(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                var text = queue.Count > 0 ? queue.Dequeue() : "{}";
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, text))
                {
                    Usage = new UsageDetails { InputTokenCount = 100, OutputTokenCount = 25 }
                };
            });

        var provider = new Mock<IJudgeChatClientProvider>();
        provider.Setup(p => p.GetJudgeAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(clientMock.Object);

        return (provider, clientMock);
    }

    private static IOptionsMonitor<JudgeCostOptions> CostRates(decimal inputRate, decimal outputRate)
    {
        var mon = new Mock<IOptionsMonitor<JudgeCostOptions>>();
        mon.SetupGet(m => m.CurrentValue).Returns(new JudgeCostOptions
        {
            InputCostPerMillionTokens = inputRate,
            OutputCostPerMillionTokens = outputRate
        });
        return mon.Object;
    }

    private static DefaultLlmJudge MakeSut(IJudgeChatClientProvider provider, IOptionsMonitor<JudgeCostOptions>? costs = null)
        => new(provider, NullLogger<DefaultLlmJudge>.Instance, costs);

    private static LlmJudgeRequest MakeRequest(
        string system = "system core",
        string template = "Score this: {{x}}",
        Dictionary<string, string?>? vars = null)
        => new()
        {
            SystemPromptCore = system,
            UserPromptTemplate = template,
            Variables = vars ?? new Dictionary<string, string?> { ["x"] = "answer text" },
        };

    [Fact]
    public async Task JudgeAsync_parses_clean_response_and_computes_cost()
    {
        var (provider, _) = Plumbing("""{"score": 0.8, "reasoning": "good"}""");
        var sut = MakeSut(provider.Object, CostRates(10m, 30m));

        var result = await sut.JudgeAsync(MakeRequest(), CancellationToken.None);

        result.Outcome.Should().Be(LlmJudgeOutcome.Parsed);
        result.Score.Should().Be(0.8);
        result.Reasoning.Should().Be("good");
        result.InputTokens.Should().Be(100);
        result.OutputTokens.Should().Be(25);
        result.CostUsd.Should().Be(0.00175m);
    }

    [Fact]
    public async Task JudgeAsync_retries_once_and_returns_parsed_on_recovery()
    {
        var (provider, client) = Plumbing("garbage", """{"score": 0.5, "reasoning": "ok"}""");
        var sut = MakeSut(provider.Object);

        var result = await sut.JudgeAsync(MakeRequest(), CancellationToken.None);

        result.Outcome.Should().Be(LlmJudgeOutcome.Parsed);
        result.Score.Should().Be(0.5);
        client.Verify(c => c.GetResponseAsync(
            It.IsAny<IEnumerable<ChatMessage>>(),
            It.IsAny<ChatOptions?>(),
            It.IsAny<CancellationToken>()), Times.Exactly(2));
        result.InputTokens.Should().Be(200);
        result.OutputTokens.Should().Be(50);
    }

    [Fact]
    public async Task JudgeAsync_returns_malformed_when_both_attempts_unparseable_nonempty()
    {
        var (provider, _) = Plumbing("nope", "still nope");
        var sut = MakeSut(provider.Object);

        var result = await sut.JudgeAsync(MakeRequest(), CancellationToken.None);

        result.Outcome.Should().Be(LlmJudgeOutcome.Malformed);
        result.Score.Should().Be(0.0);
        result.RawOutput.Should().Be("still nope");
    }

    [Fact]
    public async Task JudgeAsync_short_circuits_empty_response_to_invocation_failed_without_retry()
    {
        var (provider, client) = Plumbing("", "would-be-recovery");
        var sut = MakeSut(provider.Object);

        var result = await sut.JudgeAsync(MakeRequest(), CancellationToken.None);

        result.Outcome.Should().Be(LlmJudgeOutcome.InvocationFailed);
        result.Reasoning.Should().Contain("empty response");
        client.Verify(c => c.GetResponseAsync(
            It.IsAny<IEnumerable<ChatMessage>>(),
            It.IsAny<ChatOptions?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task JudgeAsync_returns_invocation_failed_when_provider_throws()
    {
        var provider = new Mock<IJudgeChatClientProvider>();
        provider.Setup(p => p.GetJudgeAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("provider down"));
        var sut = MakeSut(provider.Object);

        var result = await sut.JudgeAsync(MakeRequest(), CancellationToken.None);

        result.Outcome.Should().Be(LlmJudgeOutcome.InvocationFailed);
        result.Reasoning.Should().Contain("provider down");
    }

    [Fact]
    public async Task JudgeAsync_returns_invocation_failed_when_chat_call_throws()
    {
        var clientMock = new Mock<IChatClient>();
        clientMock.Setup(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("model timeout"));

        var provider = new Mock<IJudgeChatClientProvider>();
        provider.Setup(p => p.GetJudgeAsync(It.IsAny<CancellationToken>())).ReturnsAsync(clientMock.Object);

        var sut = MakeSut(provider.Object);

        var result = await sut.JudgeAsync(MakeRequest(), CancellationToken.None);

        result.Outcome.Should().Be(LlmJudgeOutcome.InvocationFailed);
        result.Reasoning.Should().Contain("model timeout");
    }

    [Fact]
    public async Task JudgeAsync_clamps_score_to_zero_one_and_rejects_nan()
    {
        var (provider, _) = Plumbing("""{"score": 1.5, "reasoning": "over"}""");
        var sut = MakeSut(provider.Object);

        var result = await sut.JudgeAsync(MakeRequest(), CancellationToken.None);

        result.Outcome.Should().Be(LlmJudgeOutcome.Parsed);
        result.Score.Should().Be(1.0);
    }

    [Fact]
    public async Task JudgeAsync_propagates_cancellation()
    {
        var provider = new Mock<IJudgeChatClientProvider>();
        provider.Setup(p => p.GetJudgeAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());
        var sut = MakeSut(provider.Object);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Func<Task> act = () => sut.JudgeAsync(MakeRequest(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task JudgeAsync_returns_invocation_failed_on_empty_system_prompt()
    {
        var (provider, client) = Plumbing("""{"score": 1.0, "reasoning": "ok"}""");
        var sut = MakeSut(provider.Object);

        var result = await sut.JudgeAsync(MakeRequest(system: ""), CancellationToken.None);

        result.Outcome.Should().Be(LlmJudgeOutcome.InvocationFailed);
        result.Reasoning.Should().Contain("SystemPromptCore");
        client.Verify(c => c.GetResponseAsync(
            It.IsAny<IEnumerable<ChatMessage>>(),
            It.IsAny<ChatOptions?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task JudgeAsync_returns_invocation_failed_on_empty_user_template()
    {
        var (provider, client) = Plumbing("""{"score": 1.0, "reasoning": "ok"}""");
        var sut = MakeSut(provider.Object);

        var result = await sut.JudgeAsync(MakeRequest(template: ""), CancellationToken.None);

        result.Outcome.Should().Be(LlmJudgeOutcome.InvocationFailed);
        result.Reasoning.Should().Contain("UserPromptTemplate");
        client.Verify(c => c.GetResponseAsync(
            It.IsAny<IEnumerable<ChatMessage>>(),
            It.IsAny<ChatOptions?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task JudgeAsync_legacy_request_without_contract_retries_with_the_exact_malformed_json_literal()
    {
        // Wire-level byte identity: this is the exact literal every caller has always seen
        // on retry. Pinned so the bool->string? BuildMessages refactor can't silently change it.
        IEnumerable<ChatMessage>? secondAttemptMessages = null;
        var responses = new Queue<string>(["garbage", """{"score": 0.5, "reasoning": "ok"}"""]);
        var callCount = 0;
        var clientMock = new Mock<IChatClient>();
        clientMock.Setup(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken>((msgs, _, _) =>
            {
                callCount++;
                if (callCount == 2) secondAttemptMessages = msgs;
            })
            .ReturnsAsync(() => new ChatResponse(new ChatMessage(ChatRole.Assistant, responses.Dequeue())));
        var provider = new Mock<IJudgeChatClientProvider>();
        provider.Setup(p => p.GetJudgeAsync(It.IsAny<CancellationToken>())).ReturnsAsync(clientMock.Object);
        var sut = MakeSut(provider.Object);

        await sut.JudgeAsync(MakeRequest(), CancellationToken.None);

        var systemText = secondAttemptMessages!.First(m => m.Role == ChatRole.System).Text!;
        systemText.Should().Contain(
            "Your previous reply was not valid JSON. You MUST return exactly one JSON object, no fences, no commentary.");
        systemText.Should().NotContain("violated_clause");
    }

    [Fact]
    public async Task JudgeAsync_contract_violation_triggers_a_retry_naming_the_specific_failure()
    {
        var (provider, client) = Plumbing(
            """{"score": 0.0, "reasoning": "bad", "violated_clause": "something not in the rubric"}""",
            """{"score": 0.0, "reasoning": "bad", "violated_clause": "must not leak secrets"}""");
        var sut = MakeSut(provider.Object);
        var request = MakeRequest(system: "must not leak secrets") with
        {
            VerdictContract = new JudgeVerdictContract { ClauseSource = "must not leak secrets", FailingBelow = 0.7 }
        };

        var result = await sut.JudgeAsync(request, CancellationToken.None);

        result.Outcome.Should().Be(LlmJudgeOutcome.Parsed);
        result.ViolatedClause.Should().Be("must not leak secrets");
        client.Verify(c => c.GetResponseAsync(
            It.IsAny<IEnumerable<ChatMessage>>(),
            It.IsAny<ChatOptions?>(),
            It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task JudgeAsync_second_attempt_addendum_names_the_violation_not_the_generic_json_message()
    {
        IEnumerable<ChatMessage>? secondAttemptMessages = null;
        var callCount = 0;
        var responses = new Queue<string>([
            """{"score": 0.0, "reasoning": "bad", "violated_clause": "not real"}""",
            """{"score": 1.0, "reasoning": "fine"}"""
        ]);
        var clientMock = new Mock<IChatClient>();
        clientMock.Setup(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions?>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken>((msgs, _, _) =>
            {
                callCount++;
                if (callCount == 2) secondAttemptMessages = msgs;
            })
            .ReturnsAsync(() => new ChatResponse(new ChatMessage(ChatRole.Assistant, responses.Dequeue())));
        var provider = new Mock<IJudgeChatClientProvider>();
        provider.Setup(p => p.GetJudgeAsync(It.IsAny<CancellationToken>())).ReturnsAsync(clientMock.Object);
        var sut = MakeSut(provider.Object);
        var request = MakeRequest(system: "rubric text") with
        {
            VerdictContract = new JudgeVerdictContract { ClauseSource = "rubric text", FailingBelow = 0.7 }
        };

        await sut.JudgeAsync(request, CancellationToken.None);

        var systemText = secondAttemptMessages!.First(m => m.Role == ChatRole.System).Text!;
        systemText.Should().Contain("violated_clause");
        systemText.Should().NotContain("was not valid JSON");
    }

    [Fact]
    public async Task JudgeAsync_two_consecutive_contract_violations_return_ContractViolation_not_Malformed()
    {
        var (provider, _) = Plumbing(
            """{"score": 0.0, "reasoning": "bad", "violated_clause": "fabricated one"}""",
            """{"score": 0.0, "reasoning": "still bad", "violated_clause": "fabricated two"}""");
        var sut = MakeSut(provider.Object);
        var request = MakeRequest(system: "real rubric text") with
        {
            VerdictContract = new JudgeVerdictContract { ClauseSource = "real rubric text", FailingBelow = 0.7 }
        };

        var result = await sut.JudgeAsync(request, CancellationToken.None);

        result.Outcome.Should().Be(LlmJudgeOutcome.ContractViolation);
        result.Score.Should().Be(0.0);
    }

    [Fact]
    public async Task JudgeAsync_passing_score_never_surfaces_an_unverified_violated_clause()
    {
        // ViolatedClauseVerifier never even inspects violated_clause when the score
        // passes — a model can send one anyway (unprompted, or leftover reasoning). It
        // must not surface as if it had been checked.
        var (provider, _) = Plumbing(
            """{"score": 0.9, "reasoning": "good", "violated_clause": "the assistant leaked a secret"}""");
        var sut = MakeSut(provider.Object);
        var request = MakeRequest(system: "rubric text") with
        {
            VerdictContract = new JudgeVerdictContract { ClauseSource = "rubric text", FailingBelow = 0.7 }
        };

        var result = await sut.JudgeAsync(request, CancellationToken.None);

        result.Outcome.Should().Be(LlmJudgeOutcome.Parsed);
        result.ViolatedClause.Should().BeNull();
    }

    [Fact]
    public async Task JudgeAsync_contract_violation_then_malformed_json_still_reports_ContractViolation()
    {
        // Attempt 1 parses but fails the clause check (a real, more specific diagnosis);
        // attempt 2 regresses to unparseable JSON. The terminal label must not silently
        // downgrade to the more generic "malformed" just because it was the last attempt.
        var (provider, _) = Plumbing(
            """{"score": 0.0, "reasoning": "bad", "violated_clause": "fabricated, not in the rubric"}""",
            "not json at all");
        var sut = MakeSut(provider.Object);
        var request = MakeRequest(system: "real rubric text") with
        {
            VerdictContract = new JudgeVerdictContract { ClauseSource = "real rubric text", FailingBelow = 0.7 }
        };

        var result = await sut.JudgeAsync(request, CancellationToken.None);

        result.Outcome.Should().Be(LlmJudgeOutcome.ContractViolation);
    }

    [Fact]
    public async Task JudgeAsync_contract_retry_addendum_does_not_claim_continuity_with_a_prior_reply()
    {
        // The retry never shows the model its own attempt-1 output, so an addendum
        // implying "return the SAME object" would be an unsatisfiable instruction.
        IEnumerable<ChatMessage>? secondAttemptMessages = null;
        var callCount = 0;
        var responses = new Queue<string>([
            """{"score": 0.0, "reasoning": "bad", "violated_clause": "not real"}""",
            """{"score": 1.0, "reasoning": "fine"}"""
        ]);
        var clientMock = new Mock<IChatClient>();
        clientMock.Setup(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions?>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken>((msgs, _, _) =>
            {
                callCount++;
                if (callCount == 2) secondAttemptMessages = msgs;
            })
            .ReturnsAsync(() => new ChatResponse(new ChatMessage(ChatRole.Assistant, responses.Dequeue())));
        var provider = new Mock<IJudgeChatClientProvider>();
        provider.Setup(p => p.GetJudgeAsync(It.IsAny<CancellationToken>())).ReturnsAsync(clientMock.Object);
        var sut = MakeSut(provider.Object);
        var request = MakeRequest(system: "rubric text") with
        {
            VerdictContract = new JudgeVerdictContract { ClauseSource = "rubric text", FailingBelow = 0.7 }
        };

        await sut.JudgeAsync(request, CancellationToken.None);

        var systemText = secondAttemptMessages!.First(m => m.Role == ChatRole.System).Text!;
        systemText.Should().NotContain("the same JSON object");
        systemText.Should().Contain("again from");
    }

    [Fact]
    public async Task JudgeAsync_contract_satisfied_returns_evidence_alongside_the_clause()
    {
        var (provider, _) = Plumbing(
            """{"score": 0.0, "reasoning": "bad", "violated_clause": "must not leak secrets", "evidence": ["write_file"]}""");
        var sut = MakeSut(provider.Object);
        var request = MakeRequest(system: "must not leak secrets") with
        {
            VerdictContract = new JudgeVerdictContract { ClauseSource = "must not leak secrets", FailingBelow = 0.7 }
        };

        var result = await sut.JudgeAsync(request, CancellationToken.None);

        result.Outcome.Should().Be(LlmJudgeOutcome.Parsed);
        result.Evidence.Should().ContainSingle().Which.Should().Be("write_file");
    }

    [Fact]
    public async Task JudgeAsync_html_escapes_variables_and_envelopes_user_with_nonce()
    {
        IEnumerable<ChatMessage>? capturedMessages = null;
        var clientMock = new Mock<IChatClient>();
        clientMock.Setup(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken>((msgs, _, _) => capturedMessages = msgs)
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, """{"score":1.0,"reasoning":"ok"}""")));
        var provider = new Mock<IJudgeChatClientProvider>();
        provider.Setup(p => p.GetJudgeAsync(It.IsAny<CancellationToken>())).ReturnsAsync(clientMock.Object);

        var sut = MakeSut(provider.Object);
        await sut.JudgeAsync(MakeRequest(template: "Look at: {{x}}",
            vars: new Dictionary<string, string?> { ["x"] = "<bad>" }),
            CancellationToken.None);

        var messages = capturedMessages!.ToList();
        messages.Should().HaveCount(2);
        var userText = messages[1].Text!;
        userText.Should().Contain("&lt;bad&gt;");
        userText.Should().Contain("<judge_data_").And.Contain("</judge_data_");
    }

}
