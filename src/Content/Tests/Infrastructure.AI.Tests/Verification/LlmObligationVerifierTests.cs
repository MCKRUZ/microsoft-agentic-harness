using Application.AI.Common.Evaluation.Interfaces;
using Application.AI.Common.Interfaces.AI;
using Application.AI.Common.Prompts.Interfaces;
using Application.AI.Common.Prompts.Models;
using Application.AI.Common.Services.Verification;
using Application.AI.Common.StructuredOutput;
using Domain.AI.Prompts;
using Domain.AI.Verification;
using FluentAssertions;
using Infrastructure.AI.Verification;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tests.Common;
using Xunit;

namespace Infrastructure.AI.Tests.Verification;

/// <summary>
/// Proves <see cref="LlmObligationVerifier"/>'s own wiring: the status→<see cref="VerificationOutcome"/>
/// mapping (including the fail-safe path for an unrecognized status), that a soft-fail
/// <see cref="StructuredOutputResult{T}"/> maps to <see cref="VerificationVerdict.VerifierError"/>
/// rather than propagating, and that the obligation triple plus artifact content actually reach
/// the model wrapped in <see cref="Application.AI.Common.Evaluation.PromptInjectionEnvelope"/>'s
/// tags. Uses a real <see cref="StructuredOutputInvoker"/> (not mocked), matching
/// <c>LlmPlanGeneratorServiceTests</c>'s precedent for testing a structured-output consumer.
/// </summary>
public sealed class LlmObligationVerifierTests
{
    private const string PromptName = "obligation-verifier-system";

    private static readonly Obligation SampleObligation =
        new(Where: "calls Foo()", ReliesOn: "def Foo() at line 40", Property: "Foo is defined");

    private readonly Mock<IJudgeChatClientProvider> _chatClientProvider = new();
    private readonly Mock<IChatClient> _chatClient = new();
    private readonly Mock<IPromptRegistry> _promptRegistry = new();
    private readonly Mock<IPromptRenderer> _promptRenderer = new();
    private readonly Mock<IPromptUsageRecorder> _usageRecorder = new();

    private readonly LlmObligationVerifier _sut;

    public LlmObligationVerifierTests()
    {
        _chatClientProvider
            .Setup(p => p.GetJudgeAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(_chatClient.Object);

        var descriptor = new PromptDescriptor
        {
            Name = PromptName,
            Version = new PromptVersion(1, 0),
            ContentHash = "deadbeef",
            Body = "verifier system prompt body",
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
                => new RenderedPrompt { Source = d, Body = "rendered-verifier-system-prompt" });
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

        _sut = new LlmObligationVerifier(
            _chatClientProvider.Object,
            structuredOutput,
            _promptRegistry.Object,
            _promptRenderer.Object,
            _usageRecorder.Object,
            new ObligationValidator(),
            NullLogger<LlmObligationVerifier>.Instance);
    }

    [Fact]
    public async Task VerifyAsync_HeldStatus_ReturnsHeldVerdict()
    {
        SetupChatClientResponse("""{ "status": "held", "explanation": "confirmed" }""");

        var verdict = await _sut.VerifyAsync(SampleObligation, "def Foo() at line 40 { }", CancellationToken.None);

        verdict.Outcome.Should().Be(VerificationOutcome.Held);
        verdict.Holds.Should().BeTrue();
    }

    [Fact]
    public async Task VerifyAsync_BrokenStatus_ReturnsBrokenVerdictWithExplanation()
    {
        SetupChatClientResponse("""{ "status": "broken", "explanation": "Foo is not defined anywhere in the artifact" }""");

        var verdict = await _sut.VerifyAsync(SampleObligation, "no such function here", CancellationToken.None);

        verdict.Outcome.Should().Be(VerificationOutcome.Broken);
        verdict.Holds.Should().BeFalse();
        verdict.Explanation.Should().Be("Foo is not defined anywhere in the artifact");
    }

    [Fact]
    public async Task VerifyAsync_UnverifiableStatus_ReturnsUnverifiableVerdict()
    {
        SetupChatClientResponse("""{ "status": "unverifiable", "explanation": "could not locate reliesOn in the artifact" }""");

        var verdict = await _sut.VerifyAsync(SampleObligation, "unrelated content", CancellationToken.None);

        verdict.Outcome.Should().Be(VerificationOutcome.Unverifiable);
        verdict.Holds.Should().BeTrue();
    }

    // Fail-safe: a status the contract doesn't define must not be silently treated as "held"
    // and must not throw — it becomes VerifierError, the same as an infrastructure failure.
    [Fact]
    public async Task VerifyAsync_UnrecognizedStatus_ReturnsVerifierError()
    {
        SetupChatClientResponse("""{ "status": "maybe", "explanation": "not sure" }""");

        var verdict = await _sut.VerifyAsync(SampleObligation, "content", CancellationToken.None);

        verdict.Outcome.Should().Be(VerificationOutcome.VerifierError);
        verdict.Holds.Should().BeTrue();
    }

    // Defense-in-depth: IObligationVerifier is registered as a public DI service, so a caller
    // that injects it directly (bypassing ObligationVerificationRunner's own filter) must still
    // get a rejected obligation caught here — and caught before any model call, not after a
    // wasted round-trip.
    [Fact]
    public async Task VerifyAsync_RejectedObligation_ReturnsVerifierErrorWithoutCallingTheModel()
    {
        var rejected = new Obligation(Where: "same text", ReliesOn: "same text", Property: "property");

        var verdict = await _sut.VerifyAsync(rejected, "content", CancellationToken.None);

        verdict.Outcome.Should().Be(VerificationOutcome.VerifierError);
        verdict.Holds.Should().BeTrue();
        _chatClient.Verify(
            c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task VerifyAsync_StatusIsCaseInsensitive()
    {
        SetupChatClientResponse("""{ "status": "HELD", "explanation": "confirmed" }""");

        var verdict = await _sut.VerifyAsync(SampleObligation, "content", CancellationToken.None);

        verdict.Outcome.Should().Be(VerificationOutcome.Held);
    }

    [Fact]
    public async Task VerifyAsync_MalformedResponse_ReturnsVerifierErrorNotThrow()
    {
        SetupChatClientResponse("this is not json");

        var verdict = await _sut.VerifyAsync(SampleObligation, "content", CancellationToken.None);

        verdict.Outcome.Should().Be(VerificationOutcome.VerifierError);
        verdict.Holds.Should().BeTrue();
    }

    [Fact]
    public async Task VerifyAsync_PromptRegistryThrowsKeyNotFound_ReturnsVerifierError()
    {
        _promptRegistry
            .Setup(r => r.GetLatestAsync(PromptName, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new KeyNotFoundException("no such prompt"));

        var verdict = await _sut.VerifyAsync(SampleObligation, "content", CancellationToken.None);

        verdict.Outcome.Should().Be(VerificationOutcome.VerifierError);
    }

    [Fact]
    public async Task VerifyAsync_SendsObligationAndArtifactContentEnvelopedInTheUserMessage()
    {
        IEnumerable<ChatMessage>? capturedMessages = null;
        _chatClient
            .Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions?>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken>((msgs, _, _) => capturedMessages = msgs)
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, """{ "status": "held", "explanation": "ok" }""")));

        var obligation = new Obligation(Where: "<b>where</b>", ReliesOn: "relies on X", Property: "must match");
        await _sut.VerifyAsync(obligation, "<script>alert(1)</script> the artifact body", CancellationToken.None);

        var messages = capturedMessages!.ToList();
        var userText = messages.Single(m => m.Role == ChatRole.User).Text!;

        userText.Should().Contain("<artifact_data_").And.Contain("</artifact_data_");
        userText.Should().Contain("&lt;script&gt;");
        userText.Should().Contain("&lt;b&gt;where&lt;/b&gt;");
        userText.Should().Contain("relies on X");
        userText.Should().Contain("must match");
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
    // against a real FilePromptRegistry pointed at the actual checked-in prompts/ directory, so
    // if PromptName drifts from the folder again, GetLatestAsync throws KeyNotFoundException
    // inside VerifyAsync and the verdict comes back VerifierError instead of a real check.
    [Fact]
    public async Task VerifyAsync_PromptNameResolvesAgainstTheRealPromptsDirectory()
    {
        var realRegistry = new Infrastructure.AI.Prompts.FilePromptRegistry(
            RepoRoot.Combine("prompts"), NullLogger<Infrastructure.AI.Prompts.FilePromptRegistry>.Instance);
        var realRenderer = new Infrastructure.AI.Prompts.ScribanPromptRenderer(NullLogger<Infrastructure.AI.Prompts.ScribanPromptRenderer>.Instance);
        var sutWithRealRegistry = new LlmObligationVerifier(
            _chatClientProvider.Object,
            new StructuredOutputInvoker(NullLogger<StructuredOutputInvoker>.Instance),
            realRegistry,
            realRenderer,
            _usageRecorder.Object,
            new ObligationValidator(),
            NullLogger<LlmObligationVerifier>.Instance);
        SetupChatClientResponse("""{ "status": "held", "explanation": "confirmed" }""");

        var verdict = await sutWithRealRegistry.VerifyAsync(SampleObligation, "content", CancellationToken.None);

        verdict.Outcome.Should().Be(VerificationOutcome.Held, because: verdict.Explanation);
    }
}
