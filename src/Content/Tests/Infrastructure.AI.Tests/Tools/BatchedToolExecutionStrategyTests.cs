using Application.AI.Common.Interfaces.Tools;
using Application.AI.Common.Models.Tools;
using Domain.AI.Models;
using Domain.AI.Tools;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.Orchestration;
using FluentAssertions;
using Infrastructure.AI.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Tools;

public sealed class BatchedToolExecutionStrategyTests
{
    private readonly Mock<IToolConcurrencyClassifier> _classifierMock = new();
    private readonly ILogger<BatchedToolExecutionStrategy> _logger = NullLogger<BatchedToolExecutionStrategy>.Instance;

    private BatchedToolExecutionStrategy CreateSut(int parallelBatchSize = 5)
    {
        var appConfig = new AppConfig
        {
            AI = new AIConfig
            {
                Orchestration = new OrchestrationConfig
                {
                    StreamingExecution = new StreamingExecutionConfig
                    {
                        ParallelBatchSize = parallelBatchSize
                    }
                }
            }
        };

        var optionsMock = new Mock<IOptionsMonitor<AppConfig>>();
        optionsMock.Setup(o => o.CurrentValue).Returns(appConfig);

        return new BatchedToolExecutionStrategy(_classifierMock.Object, optionsMock.Object, _logger);
    }

    [Fact]
    public async Task ExecuteBatch_EmptyBatch_ReturnsEmpty()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var results = await sut.ExecuteBatchAsync([]);

        // Assert
        results.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteBatch_ReadOnlyTools_RunInParallel()
    {
        // Arrange
        var concurrencyCounter = 0;
        var maxConcurrency = 0;
        var lockObj = new object();

        _classifierMock
            .Setup(c => c.Classify(It.IsAny<ITool>()))
            .Returns(ToolConcurrencyClassification.ReadOnly);

        var tool = CreateDelayTool(delay: TimeSpan.FromMilliseconds(100), onExecute: () =>
        {
            lock (lockObj)
            {
                concurrencyCounter++;
                if (concurrencyCounter > maxConcurrency)
                    maxConcurrency = concurrencyCounter;
            }
            return Task.CompletedTask;
        }, onComplete: () =>
        {
            lock (lockObj) { concurrencyCounter--; }
        });

        var requests = Enumerable.Range(0, 3)
            .Select(i => CreateRequest(tool, $"call-{i}"))
            .ToList();

        var sut = CreateSut(parallelBatchSize: 5);

        // Act
        var results = await sut.ExecuteBatchAsync(requests);

        // Assert
        results.Should().HaveCount(3);
        results.Should().AllSatisfy(r => r.Completed.Should().BeTrue());
        maxConcurrency.Should().BeGreaterThan(1, "read-only tools should run in parallel");
    }

    [Fact]
    public async Task ExecuteBatch_WriteTools_RunSerially()
    {
        // Arrange
        var concurrencyCounter = 0;
        var maxConcurrency = 0;
        var lockObj = new object();

        _classifierMock
            .Setup(c => c.Classify(It.IsAny<ITool>()))
            .Returns(ToolConcurrencyClassification.WriteSerial);

        var tool = CreateDelayTool(delay: TimeSpan.FromMilliseconds(50), onExecute: () =>
        {
            lock (lockObj)
            {
                concurrencyCounter++;
                if (concurrencyCounter > maxConcurrency)
                    maxConcurrency = concurrencyCounter;
            }
            return Task.CompletedTask;
        }, onComplete: () =>
        {
            lock (lockObj) { concurrencyCounter--; }
        });

        var requests = Enumerable.Range(0, 3)
            .Select(i => CreateRequest(tool, $"call-{i}"))
            .ToList();

        var sut = CreateSut();

        // Act
        var results = await sut.ExecuteBatchAsync(requests);

        // Assert
        results.Should().HaveCount(3);
        results.Should().AllSatisfy(r => r.Completed.Should().BeTrue());
        maxConcurrency.Should().Be(1, "write-serial tools must not run concurrently");
    }

    [Fact]
    public async Task ExecuteBatch_MixedBatch_ReadsParallelWritesSerial()
    {
        // Arrange
        var readTool = CreateImmediateTool("read_tool", ToolResult.Ok("read result"));
        var writeTool = CreateImmediateTool("write_tool", ToolResult.Ok("write result"));

        _classifierMock
            .Setup(c => c.Classify(It.Is<ITool>(t => t.Name == "read_tool")))
            .Returns(ToolConcurrencyClassification.ReadOnly);
        _classifierMock
            .Setup(c => c.Classify(It.Is<ITool>(t => t.Name == "write_tool")))
            .Returns(ToolConcurrencyClassification.WriteSerial);

        var requests = new List<ToolExecutionRequest>
        {
            CreateRequest(readTool, "call-0"),
            CreateRequest(writeTool, "call-1"),
            CreateRequest(readTool, "call-2")
        };

        var sut = CreateSut();

        // Act
        var results = await sut.ExecuteBatchAsync(requests);

        // Assert
        results.Should().HaveCount(3);
        results[0].CallId.Should().Be("call-0");
        results[0].Result.Output.Should().Be("read result");
        results[1].CallId.Should().Be("call-1");
        results[1].Result.Output.Should().Be("write result");
        results[2].CallId.Should().Be("call-2");
        results[2].Result.Output.Should().Be("read result");
    }

    [Fact]
    public async Task ExecuteBatch_ToolThrows_ErrorCapturedNotPropagated()
    {
        // Arrange
        _classifierMock
            .Setup(c => c.Classify(It.IsAny<ITool>()))
            .Returns(ToolConcurrencyClassification.WriteSerial);

        var failingTool = new Mock<ITool>();
        failingTool.Setup(t => t.Name).Returns("failing_tool");
        failingTool
            .Setup(t => t.ExecuteAsync(It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new FileNotFoundException("test file missing"));

        var successTool = CreateImmediateTool("success_tool", ToolResult.Ok("ok"));

        var requests = new List<ToolExecutionRequest>
        {
            CreateRequest(failingTool.Object, "call-fail"),
            CreateRequest(successTool, "call-ok")
        };

        var sut = CreateSut();

        // Act — should not throw
        var results = await sut.ExecuteBatchAsync(requests);

        // Assert
        results.Should().HaveCount(2);

        results[0].CallId.Should().Be("call-fail");
        results[0].Completed.Should().BeFalse();
        results[0].ErrorCategory.Should().Be("not_found");
        results[0].Result.Success.Should().BeFalse();

        results[1].CallId.Should().Be("call-ok");
        results[1].Completed.Should().BeTrue();
        results[1].Result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteBatch_ReportsProgress()
    {
        // Arrange
        _classifierMock
            .Setup(c => c.Classify(It.IsAny<ITool>()))
            .Returns(ToolConcurrencyClassification.WriteSerial);

        var tool = CreateImmediateTool("progress_tool", ToolResult.Ok("done"));
        var requests = new List<ToolExecutionRequest> { CreateRequest(tool, "call-1") };

        var progressReports = new List<ToolExecutionProgress>();
        var progress = new Progress<ToolExecutionProgress>(report => progressReports.Add(report));

        var sut = CreateSut();

        // Act
        await sut.ExecuteBatchAsync(requests, progress);

        // Allow Progress<T> callbacks to complete (they run on the thread pool)
        await Task.Delay(50);

        // Assert
        progressReports.Should().Contain(p => p.CallId == "call-1" && p.Status == "executing");
        progressReports.Should().Contain(p => p.CallId == "call-1" && p.Status == "completed");
    }

    [Fact]
    public async Task ExecuteBatch_ResultsInRequestOrder()
    {
        // Arrange — all read-only so they run in parallel, but results must be in order
        _classifierMock
            .Setup(c => c.Classify(It.IsAny<ITool>()))
            .Returns(ToolConcurrencyClassification.ReadOnly);

        var requests = Enumerable.Range(0, 5)
            .Select(i =>
            {
                var tool = CreateImmediateTool($"tool-{i}", ToolResult.Ok($"result-{i}"));
                return CreateRequest(tool, $"call-{i}");
            })
            .ToList();

        var sut = CreateSut();

        // Act
        var results = await sut.ExecuteBatchAsync(requests);

        // Assert
        for (var i = 0; i < 5; i++)
        {
            results[i].CallId.Should().Be($"call-{i}");
            results[i].Result.Output.Should().Be($"result-{i}");
        }
    }

    private static ToolExecutionRequest CreateRequest(ITool tool, string callId) =>
        new()
        {
            Tool = tool,
            Operation = "test",
            Parameters = new Dictionary<string, object?>(),
            CallId = callId
        };

    private static ITool CreateImmediateTool(string name, ToolResult result)
    {
        var mock = new Mock<ITool>();
        mock.Setup(t => t.Name).Returns(name);
        mock.Setup(t => t.ExecuteAsync(It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);
        return mock.Object;
    }

    private static ITool CreateDelayTool(TimeSpan delay, Func<Task> onExecute, Action onComplete)
    {
        var mock = new Mock<ITool>();
        mock.Setup(t => t.Name).Returns("delay_tool");
        mock.Setup(t => t.ExecuteAsync(It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .Returns(async (string _, IReadOnlyDictionary<string, object?> _, CancellationToken _) =>
            {
                await onExecute();
                await Task.Delay(delay);
                onComplete();
                return ToolResult.Ok("delayed result");
            });
        return mock.Object;
    }
}
