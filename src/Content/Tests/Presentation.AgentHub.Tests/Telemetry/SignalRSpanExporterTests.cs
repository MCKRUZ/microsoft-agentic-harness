using System.Diagnostics;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Moq;
using OpenTelemetry;
using Presentation.AgentHub.Hubs;
using Presentation.AgentHub.Telemetry;
using Xunit;

namespace Presentation.AgentHub.Tests.Telemetry;

/// <summary>
/// Unit tests for <see cref="SignalRSpanExporter"/>.
/// Tests the bounded channel backpressure, span mapping, drain loop routing,
/// and hosted service lifecycle.
/// </summary>
public sealed class SignalRSpanExporterTests : IDisposable
{
    // SimpleExportProcessor<T> is abstract to prevent direct instantiation;
    // a trivial subclass satisfies all abstract members via inheritance.
    private sealed class TestExportProcessor : SimpleExportProcessor<Activity>
    {
        public TestExportProcessor(BaseExporter<Activity> exporter) : base(exporter) { }
    }

    private readonly Mock<IClientProxy> _mockClientProxy;
    private readonly Mock<IHubClients> _mockClients;
    private readonly Mock<IHubContext<AgentTelemetryHub>> _mockHubContext;
    private readonly Mock<ILogger<SignalRSpanExporter>> _mockLogger;
    private readonly SignalRSpanExporter _exporter;
    private readonly BaseProcessor<Activity> _processor;

    public SignalRSpanExporterTests()
    {
        _mockClientProxy = new Mock<IClientProxy>();
        _mockClientProxy
            .Setup(c => c.SendCoreAsync(
                It.IsAny<string>(),
                It.IsAny<object?[]>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _mockClients = new Mock<IHubClients>();
        _mockClients
            .Setup(c => c.Group(It.IsAny<string>()))
            .Returns(_mockClientProxy.Object);

        _mockHubContext = new Mock<IHubContext<AgentTelemetryHub>>();
        _mockHubContext.Setup(h => h.Clients).Returns(_mockClients.Object);

        _mockLogger = new Mock<ILogger<SignalRSpanExporter>>();

        _exporter = new SignalRSpanExporter(_mockHubContext.Object, _mockLogger.Object);
        _processor = new TestExportProcessor(_exporter);
    }

    public void Dispose()
    {
        // TryComplete is idempotent — safe even if StopAsync was already called in the test
        _exporter.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        _exporter.Dispose();
        _processor.Dispose();
    }

    // ── Export: Channel backpressure ─────────────────────────────────────────

    [Fact]
    public void Export_ChannelFull_DoesNotBlock()
    {
        // Arrange: fill channel to capacity without a drain loop running
        for (var i = 0; i < 1000; i++)
        {
            var fill = new Activity($"fill-{i}").Start()!;
            _processor.OnEnd(fill);
            fill.Dispose();
        }

        // Act: export one more span and measure elapsed time
        var overflow = new Activity("overflow").Start()!;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _processor.OnEnd(overflow);
        sw.Stop();
        overflow.Dispose();

        // Assert: TryWrite with DropOldest returns immediately — well under 1ms
        // (100ms threshold for CI headroom)
        sw.ElapsedMilliseconds.Should().BeLessThan(100);
    }

    [Fact]
    public void Export_ChannelFull_LogsWarning()
    {
        // Arrange: fill channel to capacity
        for (var i = 0; i < 1000; i++)
        {
            var fill = new Activity($"fill-{i}").Start()!;
            _processor.OnEnd(fill);
            fill.Dispose();
        }

        // Act: export one more (wasFull = true → warning should fire)
        var overflow = new Activity("overflow").Start()!;
        _processor.OnEnd(overflow);
        overflow.Dispose();

        // Assert: a Warning log containing "dropped" or "full" was emitted
        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) =>
                    v.ToString()!.Contains("dropped", StringComparison.OrdinalIgnoreCase) ||
                    v.ToString()!.Contains("full", StringComparison.OrdinalIgnoreCase)),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce());
    }

    // ── MapToSpanData: Mapping correctness ───────────────────────────────────

    [Fact]
    public void MapToSpanData_RootSpan_SetsParentSpanIdNull()
    {
        // Arrange: activity with no parent (ParentSpanId == default)
        using var activity = new Activity("root-span").Start()!;

        // Act
        var result = SignalRSpanExporter.MapToSpanData(activity);

        // Assert
        result.ParentSpanId.Should().BeNull();
    }

    [Fact]
    public void MapToSpanData_ChildSpan_SetsParentSpanId()
    {
        // Arrange: activity with an explicit parent
        var parentTraceId = ActivityTraceId.CreateRandom();
        var parentSpanId = ActivitySpanId.CreateRandom();
        var activity = new Activity("child-span");
        activity.SetParentId(parentTraceId, parentSpanId);
        activity.Start();

        // Act
        var result = SignalRSpanExporter.MapToSpanData(activity);
        activity.Stop();

        // Assert
        result.ParentSpanId.Should().Be(parentSpanId.ToHexString());
    }

    [Fact]
    public void MapToSpanData_WithConversationIdTag_ExtractsConversationId()
    {
        // Arrange
        using var activity = new Activity("agent-span").Start()!;
        activity.SetTag("agent.conversation_id", "conv-abc");

        // Act
        var result = SignalRSpanExporter.MapToSpanData(activity);

        // Assert
        result.ConversationId.Should().Be("conv-abc");
    }

    [Fact]
    public void MapToSpanData_WithoutConversationIdTag_SetsConversationIdNull()
    {
        // Arrange: activity with no agent.conversation_id tag
        using var activity = new Activity("infra-span").Start()!;

        // Act
        var result = SignalRSpanExporter.MapToSpanData(activity);

        // Assert
        result.ConversationId.Should().BeNull();
    }

    // ── Drain loop: SignalR group routing ────────────────────────────────────

    [Fact]
    public async Task DrainLoop_SpanWithConversationId_SendsToConversationGroup()
    {
        // Arrange
        await _exporter.StartAsync(CancellationToken.None);

        var activity = new Activity("agent-span");
        activity.SetTag("agent.conversation_id", "conv-1");
        activity.Start();
        activity.Stop();
        _processor.OnEnd(activity);

        // Act: allow drain loop one processing cycle
        await Task.Delay(150);
        await _exporter.StopAsync(CancellationToken.None);

        // Assert: conversation group was targeted
        _mockClients.Verify(c => c.Group("conversation:conv-1"), Times.Once);
    }

    [Fact]
    public async Task DrainLoop_AllSpans_AlwaysSentToGlobalTraces()
    {
        // Arrange
        await _exporter.StartAsync(CancellationToken.None);

        // Span WITH conversation ID
        var withConv = new Activity("conv-span");
        withConv.SetTag("agent.conversation_id", "conv-x");
        withConv.Start();
        withConv.Stop();
        _processor.OnEnd(withConv);

        // Span WITHOUT conversation ID
        var noConv = new Activity("infra-span").Start()!;
        noConv.Stop();
        _processor.OnEnd(noConv);

        // Act: allow drain loop to process both
        await Task.Delay(150);
        await _exporter.StopAsync(CancellationToken.None);

        // Assert: global-traces group was targeted for both spans
        _mockClients.Verify(c => c.Group("global-traces"), Times.AtLeast(2));
    }

    [Fact]
    public async Task StopAsync_CompletesChannelAndDrainLoop_CompletesWithinTimeout()
    {
        // Arrange
        await _exporter.StartAsync(CancellationToken.None);

        // Act
        var stopTask = _exporter.StopAsync(CancellationToken.None);
        var completed = await Task.WhenAny(stopTask, Task.Delay(500));

        // Assert: drain loop exited without timeout
        completed.Should().Be(stopTask);
        var exception = await Record.ExceptionAsync(() => stopTask);
        exception.Should().BeNull();
    }
}
