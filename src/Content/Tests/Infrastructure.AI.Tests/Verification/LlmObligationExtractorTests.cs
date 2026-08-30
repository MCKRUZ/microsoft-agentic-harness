using Application.AI.Common.Evaluation.Interfaces;
using Application.AI.Common.Interfaces.AI;
using Application.AI.Common.Prompts.Interfaces;
using Application.AI.Common.Prompts.Models;
using Application.AI.Common.StructuredOutput;
using Domain.AI.Prompts;
using FluentAssertions;
using Infrastructure.AI.Verification;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tests.Common;
using Xunit;

namespace Infrastructure.AI.Tests.Verification;

/// <summary>
/// Proves <see cref="LlmObligationExtractor"/>'s own wiring: DTO-to-domain mapping, the
/// distinction between "found nothing" and "extraction failed" required by
/// <c>IObligationExtractor</c>'s remarks, and — the security-relevant part — that the artifact
/// content actually reaches the model wrapped in <see cref="Application.AI.Common.Evaluation.PromptInjectionEnvelope"/>'s
/// tags rather than as a bare string. Uses a real <see cref="StructuredOutputInvoker"/> (not
/// mocked) so these tests exercise the actual schema/parse/repair loop, matching
/// <c>LlmPlanGeneratorServiceTests</c>'s own precedent for testing a structured-output consumer.
/// </summary>
public sealed class LlmObligationExtractorTests
{
    private const string PromptName = "obligation-extractor";

    private readonly Mock<IJudgeChatClientProvider> _chatClientProvider = new();
    private readonly Mock<IChatClient> _chatClient = new();
    private readonly Mock<IPromptRegistry> _promptRegistry = new();
    private readonly Mock<IPromptRenderer> _promptRenderer = new();
    private readonly Mock<IPromptUsageRecorder> _usageRecorder = new();

    private readonly LlmObligationExtractor _sut;

    public LlmObligationExtractorTests()
    {
        _chatClientProvider
            .Setup(p => p.GetJudgeAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(_chatClient.Object);

        var descriptor = new PromptDescriptor
        {
            Name = PromptName,
            Version = new PromptVersion(1, 0),
            ContentHash = "deadbeef",
            Body = "extractor system prompt body",
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
                => new RenderedPrompt { Source = d, Body = "rendered-extractor-system-prompt" });
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

        _sut = new LlmObligationExtractor(
            _chatClientProvider.Object,
            structuredOutput,
            _promptRegistry.Object,
            _promptRenderer.Object,
            _usageRecorder.Object,
            NullLogger<LlmObligationExtractor>.Instance);
    }

    [Fact]
    public async Task ExtractAsync_ValidResponse_MapsDtosToObligations()
    {
        SetupChatClientResponse("""
            { "obligations": [ { "where": "line 10 calls Foo()", "reliesOn": "def Foo() at line 40", "property": "Foo is defined" } ] }
            """);

        var result = await _sut.ExtractAsync("artifact.txt", "some artifact content", CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().ContainSingle();
        result.Value![0].Where.Should().Be("line 10 calls Foo()");
        result.Value[0].ReliesOn.Should().Be("def Foo() at line 40");
        result.Value[0].Property.Should().Be("Foo is defined");
    }

    [Fact]
    public async Task ExtractAsync_NoObligationsInResponse_ReturnsSuccessWithEmptyList()
    {
        SetupChatClientResponse("""{ "obligations": [] }""");

        var result = await _sut.ExtractAsync("artifact.txt", "clean content", CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEmpty();
    }

    // The distinguishing case: "extraction ran and found nothing" (above) must NOT be
    // indistinguishable from "extraction broke" — a malformed reply that survives repair must
    // still fail, not silently collapse to Success([]).
    [Fact]
    public async Task ExtractAsync_MalformedResponse_ReturnsFailNotEmptySuccess()
    {
        SetupChatClientResponse("this is not json at all, and neither is the repair attempt");

        var result = await _sut.ExtractAsync("artifact.txt", "content", CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().NotBeEmpty();
    }

    [Fact]
    public async Task ExtractAsync_PromptRegistryThrowsKeyNotFound_ReturnsFail()
    {
        _promptRegistry
            .Setup(r => r.GetLatestAsync(PromptName, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new KeyNotFoundException("no such prompt"));

        var result = await _sut.ExtractAsync("artifact.txt", "content", CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task ExtractAsync_SendsArtifactContentEnvelopedInTheUserMessage()
    {
        IEnumerable<ChatMessage>? capturedMessages = null;
        _chatClient
            .Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions?>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken>((msgs, _, _) => capturedMessages = msgs)
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, """{ "obligations": [] }""")));

        await _sut.ExtractAsync("artifact.txt", "<script>alert(1)</script> sensitive content", CancellationToken.None);

        var messages = capturedMessages!.ToList();
        var userText = messages.Single(m => m.Role == ChatRole.User).Text!;

        userText.Should().Contain("<artifact_data_").And.Contain("</artifact_data_");
        userText.Should().Contain("&lt;script&gt;");
        userText.Should().NotContain("<script>alert(1)</script>");

        var systemText = messages.Single(m => m.Role == ChatRole.System).Text!;
        systemText.Should().Contain("extract obligations from");
    }

    [Fact]
    public async Task ExtractAsync_RecordsPromptUsage()
    {
        SetupChatClientResponse("""{ "obligations": [] }""");

        await _sut.ExtractAsync("artifact.txt", "content", CancellationToken.None);

        _usageRecorder.Verify(
            r => r.RecordAsync(It.IsAny<PromptDescriptor>(), It.IsAny<PromptUsageContext>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    private void SetupChatClientResponse(string json)
    {
        _chatClient
            .Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, json)));
    }

    // Every other test in this file mocks IPromptRegistry, which cannot catch the sut's own
    // PromptName constant drifting from the real prompts/{name}/ folder — exactly the bug a
    // prior review round found here (PromptName said "obligation-extractor-system", the folder
    // was "obligation-extractor", every mocked test still passed). This test wires the SUT
    // against a real FilePromptRegistry pointed at the actual checked-in prompts/ directory, so
    // if PromptName drifts from the folder again, GetLatestAsync throws KeyNotFoundException
    // inside ExtractAsync and the result comes back a failure instead of IsSuccess.
    [Fact]
    public async Task ExtractAsync_PromptNameResolvesAgainstTheRealPromptsDirectory()
    {
        var realRegistry = new Infrastructure.AI.Prompts.FilePromptRegistry(
            RepoRoot.Combine("prompts"), NullLogger<Infrastructure.AI.Prompts.FilePromptRegistry>.Instance);
        var realRenderer = new Infrastructure.AI.Prompts.ScribanPromptRenderer(NullLogger<Infrastructure.AI.Prompts.ScribanPromptRenderer>.Instance);
        var sutWithRealRegistry = new LlmObligationExtractor(
            _chatClientProvider.Object,
            new StructuredOutputInvoker(NullLogger<StructuredOutputInvoker>.Instance),
            realRegistry,
            realRenderer,
            _usageRecorder.Object,
            NullLogger<LlmObligationExtractor>.Instance);
        SetupChatClientResponse("""{ "obligations": [] }""");

        var result = await sutWithRealRegistry.ExtractAsync("artifact.txt", "content", CancellationToken.None);

        result.IsSuccess.Should().BeTrue(because: string.Join("; ", result.Errors));
    }
}
