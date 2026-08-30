using Application.AI.Common.Evaluation.Interfaces;
using Application.AI.Common.Interfaces.AI;
using Application.AI.Common.Prompts.Interfaces;
using Application.AI.Common.Prompts.Models;
using Application.AI.Common.StructuredOutput;
using Domain.AI.ClaimVerification;
using Domain.AI.Prompts;
using Domain.Common.Config;
using FluentAssertions;
using Infrastructure.AI.Verification;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Tests.Common;
using Xunit;

namespace Infrastructure.AI.Tests.Verification;

/// <summary>
/// Proves <see cref="LlmClaimVerifier"/>'s own wiring: the status→<see cref="ClaimVerificationOutcome"/>
/// mapping (including the fail-safe path for an unrecognized status), that a soft-fail
/// <see cref="StructuredOutputResult{T}"/> maps to <see cref="ClaimVerdict.VerifierError"/> rather
/// than propagating, and that the claim plus evidence content actually reach the model wrapped in
/// <see cref="Application.AI.Common.Evaluation.PromptInjectionEnvelope"/>'s tags. Uses a real
/// <see cref="StructuredOutputInvoker"/> (not mocked), matching <c>LlmObligationVerifierTests</c>'s
/// precedent for testing a structured-output consumer.
/// </summary>
public sealed class LlmClaimVerifierTests
{
    private const string PromptName = "claim-verifier";

    private static readonly Claim SampleClaim = new()
    {
        Text = "The current value of RetryConfig.MaxAttempts is 2.",
        Location = "config:AI.Resilience.Retry.MaxAttempts",
        ConsequenceSignals = new ClaimConsequenceSignals { CausesWrite = false, GatesADecision = true }
    };

    private readonly Mock<IJudgeChatClientProvider> _chatClientProvider = new();
    private readonly Mock<IChatClient> _chatClient = new();
    private readonly Mock<IPromptRegistry> _promptRegistry = new();
    private readonly Mock<IPromptRenderer> _promptRenderer = new();
    private readonly Mock<IPromptUsageRecorder> _usageRecorder = new();

    // Enabled=true by default in this fixture: every test in this file exercises the model-call
    // path, so the feature must be on. The Enabled=false gate is its own dedicated test below,
    // built with a separate SUT instance rather than mutating this shared config.
    private readonly IOptionsMonitor<AppConfig> _config = new StaticOptionsMonitor<AppConfig>(
        new AppConfig { AI = { ClaimVerification = { Enabled = true } } });

    private readonly LlmClaimVerifier _sut;

    public LlmClaimVerifierTests()
    {
        _chatClientProvider
            .Setup(p => p.GetJudgeAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(_chatClient.Object);

        var descriptor = new PromptDescriptor
        {
            Name = PromptName,
            Version = new PromptVersion(1, 0),
            ContentHash = "deadbeef",
            Body = "claim verifier system prompt body",
        };
        _promptRegistry
            .Setup(r => r.GetLatestAsync(PromptName, It.IsAny<CancellationToken>()))
            .ReturnsAsync(descriptor);
        _promptRenderer
            .Setup(r => r.RenderAsync(
                It.IsAny<PromptDescriptor>(),
                It.IsAny<IReadOnlyDictionary<string, object?>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((PromptDescriptor d, IReadOnlyDictionary<string, object?> _, CancellationToken __)
                => new RenderedPrompt { Source = d, Body = "rendered-claim-verifier-system-prompt" });
        _usageRecorder
            .Setup(r => r.RecordAsync(It.IsAny<PromptDescriptor>(), It.IsAny<PromptUsageContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PromptDescriptor d, PromptUsageContext c, CancellationToken _) => new PromptUsageRecord
            {
                Descriptor = d,
                CaseId = c.CaseId,
                MetricKey = c.MetricKey,
                RecordedAtUtc = DateTimeOffset.UtcNow,
            });

        var structuredOutput = new StructuredOutputInvoker(NullLogger<StructuredOutputInvoker>.Instance);

        _sut = new LlmClaimVerifier(
            _chatClientProvider.Object,
            structuredOutput,
            _promptRegistry.Object,
            _promptRenderer.Object,
            _usageRecorder.Object,
            _config,
            NullLogger<LlmClaimVerifier>.Instance);
    }

    [Fact]
    public async Task VerifyAsync_HeldStatus_ReturnsHeldVerdict()
    {
        SetupChatClientResponse("""{ "status": "held", "explanation": "confirmed" }""");

        var verdict = await _sut.VerifyAsync(SampleClaim, "MaxAttempts = 2", CancellationToken.None);

        verdict.Outcome.Should().Be(ClaimVerificationOutcome.Held);
        verdict.RevisedClaim.Confidence.Should().Be(SampleClaim.Confidence);
    }

    [Fact]
    public async Task VerifyAsync_BrokenStatus_ReturnsBrokenVerdictWithFlooredConfidence()
    {
        SetupChatClientResponse("""{ "status": "broken", "explanation": "MaxAttempts is actually 3" }""");

        var verdict = await _sut.VerifyAsync(SampleClaim, "MaxAttempts = 3", CancellationToken.None);

        verdict.Outcome.Should().Be(ClaimVerificationOutcome.Broken);
        verdict.RevisedClaim.Confidence.Should().Be(0.1);
        verdict.Explanation.Should().Be("MaxAttempts is actually 3");
    }

    [Fact]
    public async Task VerifyAsync_UnverifiableStatus_ReturnsUnverifiableVerdictUnchanged()
    {
        SetupChatClientResponse("""{ "status": "unverifiable", "explanation": "evidence does not address the claim" }""");

        var verdict = await _sut.VerifyAsync(SampleClaim, "unrelated evidence", CancellationToken.None);

        verdict.Outcome.Should().Be(ClaimVerificationOutcome.Unverifiable);
        verdict.RevisedClaim.Confidence.Should().Be(SampleClaim.Confidence);
    }

    // Fail-safe: a status the contract doesn't define must not be silently treated as "held" and
    // must not throw — it becomes VerifierError, the same as an infrastructure failure.
    [Fact]
    public async Task VerifyAsync_UnrecognizedStatus_ReturnsVerifierError()
    {
        SetupChatClientResponse("""{ "status": "maybe", "explanation": "not sure" }""");

        var verdict = await _sut.VerifyAsync(SampleClaim, "content", CancellationToken.None);

        verdict.Outcome.Should().Be(ClaimVerificationOutcome.VerifierError);
        verdict.RevisedClaim.Confidence.Should().Be(SampleClaim.Confidence);
    }

    // Same RespectNullableAnnotations gap LlmObligationVerifierTests documents: `required` on
    // ClaimVerificationResponse.Explanation is a constructor-time guarantee only.
    [Fact]
    public async Task VerifyAsync_BrokenStatusWithNullExplanation_SubstitutesPlaceholderNotNull()
    {
        SetupChatClientResponse("""{ "status": "broken", "explanation": null }""");

        var verdict = await _sut.VerifyAsync(SampleClaim, "content", CancellationToken.None);

        verdict.Outcome.Should().Be(ClaimVerificationOutcome.Broken);
        verdict.Explanation.Should().NotBeNullOrWhiteSpace();
    }

    // Same RespectNullableAnnotations gap, on Status this time: a model returning
    // {"status":null,...} deserializes successfully despite `required`. Must fail safe to
    // VerifierError, not throw an NRE out of .Trim().
    [Fact]
    public async Task VerifyAsync_NullStatus_ReturnsVerifierErrorNotThrow()
    {
        SetupChatClientResponse("""{ "status": null, "explanation": "whatever" }""");

        var verdict = await _sut.VerifyAsync(SampleClaim, "content", CancellationToken.None);

        verdict.Outcome.Should().Be(ClaimVerificationOutcome.VerifierError);
    }

    [Fact]
    public async Task VerifyAsync_StatusIsCaseInsensitive()
    {
        SetupChatClientResponse("""{ "status": "HELD", "explanation": "confirmed" }""");

        var verdict = await _sut.VerifyAsync(SampleClaim, "content", CancellationToken.None);

        verdict.Outcome.Should().Be(ClaimVerificationOutcome.Held);
    }

    [Fact]
    public async Task VerifyAsync_MalformedResponse_ReturnsVerifierErrorNotThrow()
    {
        SetupChatClientResponse("this is not json");

        var verdict = await _sut.VerifyAsync(SampleClaim, "content", CancellationToken.None);

        verdict.Outcome.Should().Be(ClaimVerificationOutcome.VerifierError);
    }

    [Fact]
    public async Task VerifyAsync_PromptRegistryThrowsKeyNotFound_ReturnsVerifierError()
    {
        _promptRegistry
            .Setup(r => r.GetLatestAsync(PromptName, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new KeyNotFoundException("no such prompt"));

        var verdict = await _sut.VerifyAsync(SampleClaim, "content", CancellationToken.None);

        verdict.Outcome.Should().Be(ClaimVerificationOutcome.VerifierError);
    }

    [Fact]
    public async Task VerifyAsync_PromptRendererThrows_ReturnsVerifierErrorNotThrow()
    {
        _promptRenderer
            .Setup(r => r.RenderAsync(
                It.IsAny<PromptDescriptor>(),
                It.IsAny<IReadOnlyDictionary<string, object?>>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("render blew up"));

        var verdict = await _sut.VerifyAsync(SampleClaim, "content", CancellationToken.None);

        verdict.Outcome.Should().Be(ClaimVerificationOutcome.VerifierError);
    }

    [Fact]
    public async Task VerifyAsync_UsageRecorderThrows_ReturnsVerifierErrorNotThrow()
    {
        _usageRecorder
            .Setup(r => r.RecordAsync(It.IsAny<PromptDescriptor>(), It.IsAny<PromptUsageContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("recording blew up"));

        var verdict = await _sut.VerifyAsync(SampleClaim, "content", CancellationToken.None);

        verdict.Outcome.Should().Be(ClaimVerificationOutcome.VerifierError);
    }

    [Fact]
    public async Task VerifyAsync_SendsClaimAndEvidenceEnvelopedInTheUserMessage()
    {
        IEnumerable<ChatMessage>? capturedMessages = null;
        _chatClient
            .Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions?>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken>((msgs, _, _) => capturedMessages = msgs)
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, """{ "status": "held", "explanation": "ok" }""")));

        var claim = new Claim
        {
            Text = "<b>the claim</b>",
            Location = "file:src/Foo.cs",
            ConsequenceSignals = new ClaimConsequenceSignals { CausesWrite = false, GatesADecision = true }
        };
        await _sut.VerifyAsync(claim, "<script>alert(1)</script> the evidence body", CancellationToken.None);

        var messages = capturedMessages!.ToList();
        var userText = messages.Single(m => m.Role == ChatRole.User).Text!;

        userText.Should().Contain("<evidence_data_").And.Contain("</evidence_data_");
        userText.Should().Contain("&lt;script&gt;");
        userText.Should().Contain("&lt;b&gt;the claim&lt;/b&gt;");
        userText.Should().NotContain("<script>alert(1)</script>");

        var systemText = messages.Single(m => m.Role == ChatRole.System).Text!;
        systemText.Should().Contain("verify");
    }

    private void SetupChatClientResponse(string json)
    {
        _chatClient
            .Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, json)));
    }

    // Every other test in this file mocks IPromptRegistry, which cannot catch the sut's own
    // PromptName constant drifting from the real prompts/{name}/ folder. This test wires the SUT
    // against a real FilePromptRegistry pointed at the actual checked-in prompts/ directory.
    [Fact]
    public async Task VerifyAsync_PromptNameResolvesAgainstTheRealPromptsDirectory()
    {
        var realRegistry = new Infrastructure.AI.Prompts.FilePromptRegistry(
            RepoRoot.Combine("prompts"), NullLogger<Infrastructure.AI.Prompts.FilePromptRegistry>.Instance);
        var realRenderer = new Infrastructure.AI.Prompts.ScribanPromptRenderer(NullLogger<Infrastructure.AI.Prompts.ScribanPromptRenderer>.Instance);
        var sutWithRealRegistry = new LlmClaimVerifier(
            _chatClientProvider.Object,
            new StructuredOutputInvoker(NullLogger<StructuredOutputInvoker>.Instance),
            realRegistry,
            realRenderer,
            _usageRecorder.Object,
            _config,
            NullLogger<LlmClaimVerifier>.Instance);
        SetupChatClientResponse("""{ "status": "held", "explanation": "confirmed" }""");

        var verdict = await sutWithRealRegistry.VerifyAsync(SampleClaim, "content", CancellationToken.None);

        verdict.Outcome.Should().Be(ClaimVerificationOutcome.Held, because: verdict.Explanation);
    }

    // AppConfig:AI:ClaimVerification:Enabled gates the judge call itself — a disabled host must
    // never send claim/evidence content to a model, and must fail safe (Unverifiable) rather than
    // throw. Deleting this check restores the dead-control defect: setting Enabled=false would no
    // longer stop a live judge call.
    [Fact]
    public async Task VerifyAsync_ClaimVerificationDisabled_ReturnsUnverifiableWithoutCallingTheModel()
    {
        var disabledSut = new LlmClaimVerifier(
            _chatClientProvider.Object,
            new StructuredOutputInvoker(NullLogger<StructuredOutputInvoker>.Instance),
            _promptRegistry.Object,
            _promptRenderer.Object,
            _usageRecorder.Object,
            new StaticOptionsMonitor<AppConfig>(new AppConfig { AI = { ClaimVerification = { Enabled = false } } }),
            NullLogger<LlmClaimVerifier>.Instance);

        var verdict = await disabledSut.VerifyAsync(SampleClaim, "content", CancellationToken.None);

        verdict.Outcome.Should().Be(ClaimVerificationOutcome.Unverifiable);
        _chatClient.Verify(
            c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public StaticOptionsMonitor(T value) { CurrentValue = value; }
        public T CurrentValue { get; }
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
