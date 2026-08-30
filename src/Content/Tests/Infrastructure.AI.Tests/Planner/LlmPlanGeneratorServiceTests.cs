using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Planner;
using Application.AI.Common.Prompts.Interfaces;
using Application.AI.Common.Prompts.Models;
using Application.AI.Common.StructuredOutput;
using Domain.AI.Planner;
using Domain.AI.Prompts;
using Domain.Common;
using Domain.Common.Config.AI;
using Infrastructure.AI.Planner;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Planner;

public sealed class LlmPlanGeneratorServiceTests
{
    private readonly Mock<IChatClientFactory> _chatClientFactory = new();
    private readonly Mock<IPlanValidator> _validator = new();
    private readonly Mock<IChatClient> _chatClient = new();
    private readonly Mock<IPromptRegistry> _promptRegistry = new();
    private readonly Mock<IPromptRenderer> _promptRenderer = new();
    private readonly Mock<IPromptUsageRecorder> _usageRecorder = new();
    private readonly PlannerOptions _options = new()
    {
        GenerationModel = "gpt-4o",
        ClientType = AIAgentFrameworkClientType.AzureOpenAI,
        GenerationTemperature = 0.3,
        GenerationMaxTokens = 4096
    };

    private readonly LlmPlanGeneratorService _sut;

    public LlmPlanGeneratorServiceTests()
    {
        _chatClientFactory
            .Setup(f => f.GetChatClientAsync(
                It.IsAny<AIAgentFrameworkClientType>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(_chatClient.Object);

        var descriptor = new PromptDescriptor
        {
            Name = "plan-generator-system",
            Version = new PromptVersion(1, 0),
            ContentHash = "deadbeef",
            Body = "system prompt body {{ constraints_block }}",
        };
        _promptRegistry
            .Setup(r => r.GetLatestAsync("plan-generator-system", It.IsAny<CancellationToken>()))
            .ReturnsAsync(descriptor);
        _promptRenderer
            .Setup(r => r.RenderAsync(
                It.IsAny<PromptDescriptor>(),
                It.IsAny<IReadOnlyDictionary<string, object?>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((PromptDescriptor d, IReadOnlyDictionary<string, object?> _, CancellationToken __)
                => new RenderedPrompt { Source = d, Body = "rendered-system-prompt" });
        _usageRecorder
            .Setup(r => r.RecordAsync(
                It.IsAny<PromptDescriptor>(),
                It.IsAny<PromptUsageContext>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((PromptDescriptor d, PromptUsageContext c, CancellationToken _) => new PromptUsageRecord
            {
                Descriptor = d,
                CaseId = c.CaseId,
                MetricKey = c.MetricKey,
                RecordedAtUtc = DateTimeOffset.UtcNow,
            });

        var optionsMonitor = Mock.Of<IOptionsMonitor<PlannerOptions>>(
            o => o.CurrentValue == _options);

        // Real implementation, not a mock — drives the actual schema-out/parse-back/repair loop so
        // these tests prove the wiring works end to end, not just that a mocked invoker was called.
        var structuredOutput = new StructuredOutputInvoker(NullLogger<StructuredOutputInvoker>.Instance);

        _sut = new LlmPlanGeneratorService(
            _chatClientFactory.Object,
            structuredOutput,
            _promptRegistry.Object,
            _promptRenderer.Object,
            _usageRecorder.Object,
            _validator.Object,
            optionsMonitor,
            NullLogger<LlmPlanGeneratorService>.Instance);
    }

    [Fact]
    public async Task GenerateAsync_ValidTask_ReturnsPlanGraph()
    {
        SetupChatClientResponse(ValidThreeStepPlanJson);
        SetupValidatorSuccess();

        var result = await _sut.GenerateAsync("Build a REST API");

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal(3, result.Value!.Steps.Count);
        Assert.Equal(2, result.Value.Edges.Count);
        Assert.NotEqual(Guid.Empty, result.Value.Id.Value);
        Assert.All(result.Value.Steps, s => Assert.NotEqual(Guid.Empty, s.Id.Value));
    }

    [Fact]
    public async Task GenerateAsync_LlmOutput_ValidatedBeforeReturn()
    {
        SetupChatClientResponse(ValidThreeStepPlanJson);
        SetupValidatorSuccess();

        await _sut.GenerateAsync("any task");

        _validator.Verify(
            v => v.ValidateAsync(It.IsAny<PlanGraph>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task GenerateAsync_InvalidLlmOutput_ReturnsFail()
    {
        SetupChatClientResponse("{ invalid json missing fields");

        var result = await _sut.GenerateAsync("any task");

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors, e => e.Contains("JSON", StringComparison.OrdinalIgnoreCase));
        _validator.Verify(
            v => v.ValidateAsync(It.IsAny<PlanGraph>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task GenerateAsync_ValidationFails_ReturnsFail()
    {
        SetupChatClientResponse(ValidThreeStepPlanJson);
        _validator
            .Setup(v => v.ValidateAsync(It.IsAny<PlanGraph>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<PlanValidationResult>.Success(new PlanValidationResult
            {
                IsValid = false,
                Errors = ["Cycle detected: step-a -> step-b -> step-a"]
            }));

        var result = await _sut.GenerateAsync("any task");

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors, e => e.Contains("Cycle detected"));
    }

    [Fact]
    public async Task GenerateAsync_WithConstraints_PassesConstraintsBlockToRenderer()
    {
        // The renderer is mocked, so we can't verify the rendered body literally —
        // instead we capture the variables passed to RenderAsync and assert the
        // constraints_block string contains the requested values.
        string? capturedConstraintsBlock = null;
        _promptRenderer
            .Setup(r => r.RenderAsync(
                It.IsAny<PromptDescriptor>(),
                It.IsAny<IReadOnlyDictionary<string, object?>>(),
                It.IsAny<CancellationToken>()))
            .Callback<PromptDescriptor, IReadOnlyDictionary<string, object?>, CancellationToken>(
                (d, vars, _) => capturedConstraintsBlock = vars["constraints_block"]?.ToString())
            .ReturnsAsync((PromptDescriptor d, IReadOnlyDictionary<string, object?> _, CancellationToken __)
                => new RenderedPrompt { Source = d, Body = "rendered" });

        _chatClient
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IList<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateChatResponse(ValidThreeStepPlanJson));
        SetupValidatorSuccess();

        var constraints = new PlanGenerationConstraints
        {
            MaxSteps = 5,
            AllowedStepTypes = [StepType.LlmCall, StepType.ToolUse]
        };

        await _sut.GenerateAsync("Build API", constraints);

        Assert.NotNull(capturedConstraintsBlock);
        Assert.Contains("5", capturedConstraintsBlock);
        Assert.Contains("LlmCall", capturedConstraintsBlock);
        Assert.Contains("ToolUse", capturedConstraintsBlock);
    }

    [Fact]
    public async Task GenerateAsync_LlmReturnsEmptyResponse_ReturnsFail()
    {
        _chatClient
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IList<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateChatResponse(""));

        var result = await _sut.GenerateAsync("any task");

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors, e => e.Contains("empty", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GenerateAsync_CancellationRequested_Throws()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => _sut.GenerateAsync("any task", ct: cts.Token));
    }

    [Fact]
    public async Task GenerateAsync_AssignsPlanIdAndStepIds()
    {
        SetupChatClientResponse(ValidThreeStepPlanJson);
        SetupValidatorSuccess();

        var result = await _sut.GenerateAsync("any task");

        Assert.True(result.IsSuccess);
        var graph = result.Value!;
        Assert.NotEqual(default, graph.Id);

        var stepIds = graph.Steps.Select(s => s.Id).ToList();
        Assert.Equal(stepIds.Count, stepIds.Distinct().Count());
        Assert.All(stepIds, id => Assert.NotEqual(Guid.Empty, id.Value));

        Assert.All(graph.Edges, edge =>
        {
            Assert.Contains(edge.From, stepIds);
            Assert.Contains(edge.To, stepIds);
        });
    }

    private void SetupChatClientResponse(string json)
    {
        _chatClient
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IList<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateChatResponse(json));
    }

    private void SetupValidatorSuccess()
    {
        _validator
            .Setup(v => v.ValidateAsync(It.IsAny<PlanGraph>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<PlanValidationResult>.Success(new PlanValidationResult
            {
                IsValid = true,
                EstimatedCriticalPathDuration = TimeSpan.FromMinutes(5)
            }));
    }

    private static ChatResponse CreateChatResponse(string content)
    {
        var message = new ChatMessage(ChatRole.Assistant, content);
        return new ChatResponse([message]);
    }

    private const string ValidThreeStepPlanJson = """
        {
          "name": "Build REST API",
          "steps": [
            {
              "name": "Design schema",
              "type": "LlmCall",
              "configuration": {
                "type": "llm_call",
                "systemPrompt": "Design the database schema",
                "modelDeploymentKey": "gpt-4o",
                "temperature": 0.7,
                "maxTokens": 4096
              },
              "retryPolicy": { "maxRetries": 3, "initialDelayMs": 1000, "strategy": "Exponential", "onExhausted": "FailStep" },
              "timeoutSeconds": 60
            },
            {
              "name": "Generate endpoints",
              "type": "ToolUse",
              "configuration": {
                "type": "tool_use",
                "toolName": "code_generator",
                "inputParameters": { "language": "csharp" }
              },
              "retryPolicy": { "maxRetries": 2, "initialDelayMs": 500, "strategy": "Fixed", "onExhausted": "FailStep" },
              "timeoutSeconds": 120
            },
            {
              "name": "Review output",
              "type": "HumanGate",
              "configuration": {
                "type": "human_gate",
                "description": "Review the generated API endpoints",
                "timeoutMinutes": 60
              },
              "retryPolicy": { "maxRetries": 0, "initialDelayMs": 0, "strategy": "Fixed", "onExhausted": "FailStep" },
              "timeoutSeconds": 3600
            }
          ],
          "edges": [
            { "from": "Design schema", "to": "Generate endpoints", "type": "DataFlow" },
            { "from": "Generate endpoints", "to": "Review output", "type": "ControlFlow" }
          ],
          "configuration": {
            "planTimeoutMinutes": 30,
            "maxParallelSteps": 10,
            "maxSubPlanDepth": 5
          }
        }
        """;
}
