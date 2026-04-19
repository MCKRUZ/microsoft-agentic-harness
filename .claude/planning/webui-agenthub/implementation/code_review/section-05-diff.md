diff --git a/src/Content/Presentation/Presentation.AgentHub/DependencyInjection.cs b/src/Content/Presentation/Presentation.AgentHub/DependencyInjection.cs
index e58188e..66c6885 100644
--- a/src/Content/Presentation/Presentation.AgentHub/DependencyInjection.cs
+++ b/src/Content/Presentation/Presentation.AgentHub/DependencyInjection.cs
@@ -1,12 +1,16 @@
+using System.Diagnostics;
+using System.Threading.RateLimiting;
 using Microsoft.AspNetCore.Authentication.JwtBearer;
 using Microsoft.AspNetCore.Http;
 using Microsoft.AspNetCore.RateLimiting;
 using Microsoft.Identity.Web;
+using OpenTelemetry;
+using OpenTelemetry.Trace;
 using Presentation.AgentHub.Hubs;
 using Presentation.AgentHub.Interfaces;
 using Presentation.AgentHub.Models;
 using Presentation.AgentHub.Services;
-using System.Threading.RateLimiting;
+using Presentation.AgentHub.Telemetry;
 
 namespace Presentation.AgentHub;
 
@@ -119,10 +123,31 @@ public static class DependencyInjection
         // Singleton: ConversationLockRegistry must outlive hub instances (hubs are transient).
         services.AddSingleton<ConversationLockRegistry>();
 
-        // Section 5 — SignalRSpanExporter
-        // services.AddSingleton<SignalRSpanExporter>();
-        // services.AddHostedService(sp => sp.GetRequiredService<SignalRSpanExporter>());
+        // SignalRSpanExporter bridges OTel Activity pipeline → SignalR.
+        // Registered as singleton so the same instance is both the IHostedService (drain loop)
+        // and the BaseExporter<Activity> added to the OTel tracing pipeline below.
+        services.AddSingleton<SignalRSpanExporter>();
+        services.AddHostedService(sp => sp.GetRequiredService<SignalRSpanExporter>());
+
+        // Append SignalRSpanExporter to the OTel tracing pipeline AFTER GetServices() has run
+        // and Infrastructure.Observability's ITelemetryConfigurator (order 300) has already
+        // registered Jaeger / Azure Monitor exporters. Using AddOpenTelemetry().WithTracing()
+        // here appends without touching Infrastructure.Observability's DI code.
+        // AgentHubSpanExportProcessor is a file-private concrete subclass of
+        // SimpleExportProcessor<Activity> (which is abstract to prevent direct instantiation).
+        services.AddOpenTelemetry()
+            .WithTracing(b => b.AddProcessor(
+                sp => new AgentHubSpanExportProcessor(
+                    sp.GetRequiredService<SignalRSpanExporter>())));
 
         return services;
     }
 }
+
+/// <summary>
+/// Concrete <see cref="SimpleExportProcessor{T}"/> wrapping <see cref="SignalRSpanExporter"/> for
+/// registration in the OTel tracing pipeline. File-scoped to keep it an implementation detail of
+/// <see cref="DependencyInjection"/>.
+/// </summary>
+file sealed class AgentHubSpanExportProcessor(SignalRSpanExporter exporter)
+    : SimpleExportProcessor<Activity>(exporter);
diff --git a/src/Content/Presentation/Presentation.AgentHub/Presentation.AgentHub.csproj b/src/Content/Presentation/Presentation.AgentHub/Presentation.AgentHub.csproj
index a8f91d4..a9a94ba 100644
--- a/src/Content/Presentation/Presentation.AgentHub/Presentation.AgentHub.csproj
+++ b/src/Content/Presentation/Presentation.AgentHub/Presentation.AgentHub.csproj
@@ -13,8 +13,15 @@
   <ItemGroup>
     <!-- OpenTelemetry — versions already in Directory.Packages.props -->
     <PackageReference Include="OpenTelemetry" />
+    <PackageReference Include="OpenTelemetry.Extensions.Hosting" />
     <!-- Microsoft.Identity.Web — already in Directory.Packages.props -->
     <PackageReference Include="Microsoft.Identity.Web" />
   </ItemGroup>
 
+  <ItemGroup>
+    <AssemblyAttribute Include="System.Runtime.CompilerServices.InternalsVisibleTo">
+      <_Parameter1>Presentation.AgentHub.Tests</_Parameter1>
+    </AssemblyAttribute>
+  </ItemGroup>
+
 </Project>
diff --git a/src/Content/Presentation/Presentation.AgentHub/Telemetry/SignalRSpanExporter.cs b/src/Content/Presentation/Presentation.AgentHub/Telemetry/SignalRSpanExporter.cs
new file mode 100644
index 0000000..cf0976b
--- /dev/null
+++ b/src/Content/Presentation/Presentation.AgentHub/Telemetry/SignalRSpanExporter.cs
@@ -0,0 +1,171 @@
+using System.Diagnostics;
+using System.Threading.Channels;
+using Microsoft.AspNetCore.SignalR;
+using Microsoft.Extensions.Hosting;
+using Microsoft.Extensions.Logging;
+using OpenTelemetry;
+using Presentation.AgentHub.Hubs;
+
+namespace Presentation.AgentHub.Telemetry;
+
+/// <summary>
+/// Custom OpenTelemetry exporter that bridges the OTel Activity pipeline to SignalR clients in real time.
+/// Implements <see cref="BaseExporter{T}"/> for synchronous export on the OTel SDK thread and
+/// <see cref="IHostedService"/> for asynchronous drain to SignalR on a dedicated background loop.
+/// </summary>
+/// <remarks>
+/// <para>
+/// <see cref="Export"/> is called synchronously from the OTel SDK background thread where <c>await</c>
+/// is unavailable. Calling SignalR's <c>SendAsync</c> directly would block the pipeline under load.
+/// Instead, <see cref="Export"/> enqueues <see cref="SpanData"/> to a bounded
+/// <see cref="Channel{T}"/> via non-blocking <c>TryWrite</c>.
+/// </para>
+/// <para>
+/// <see cref="StartAsync"/> launches <see cref="DrainAsync"/> which dequeues spans and calls
+/// <c>SendAsync</c> using <c>await Task.WhenAll</c> — never <c>Task.Run</c> — to preserve ordering
+/// and surface exceptions without unbounded task spawning.
+/// </para>
+/// <para>
+/// The channel is bounded at 1000 with <see cref="BoundedChannelFullMode.DropOldest"/>.
+/// When the channel is at capacity a warning is logged and the oldest span is silently replaced,
+/// ensuring backpressure never stalls the agent pipeline.
+/// </para>
+/// </remarks>
+public sealed class SignalRSpanExporter : BaseExporter<Activity>, IHostedService
+{
+    private const int ChannelCapacity = 1000;
+
+    private readonly IHubContext<AgentTelemetryHub> _hubContext;
+    private readonly ILogger<SignalRSpanExporter> _logger;
+    private readonly Channel<SpanData> _channel;
+    private Task _drainTask = Task.CompletedTask;
+
+    /// <summary>
+    /// Initializes a new instance of <see cref="SignalRSpanExporter"/>.
+    /// </summary>
+    /// <param name="hubContext">SignalR hub context used to broadcast spans to connected clients.</param>
+    /// <param name="logger">Logger for backpressure warnings.</param>
+    public SignalRSpanExporter(
+        IHubContext<AgentTelemetryHub> hubContext,
+        ILogger<SignalRSpanExporter> logger)
+    {
+        _hubContext = hubContext;
+        _logger = logger;
+        _channel = Channel.CreateBounded<SpanData>(new BoundedChannelOptions(ChannelCapacity)
+        {
+            FullMode = BoundedChannelFullMode.DropOldest
+        });
+    }
+
+    /// <summary>
+    /// Called synchronously by the OTel SDK. Writes each span to the bounded channel.
+    /// Never blocks — if the channel is at capacity the oldest span is dropped and a warning is logged.
+    /// Always returns <see cref="ExportResult.Success"/>.
+    /// </summary>
+    public override ExportResult Export(in Batch<Activity> batch)
+    {
+        foreach (var activity in batch)
+        {
+            var data = MapToSpanData(activity);
+            var wasFull = _channel.Reader.Count >= ChannelCapacity;
+            _channel.Writer.TryWrite(data);
+            if (wasFull)
+            {
+                _logger.LogWarning(
+                    "OTel channel full — span dropped: {SpanName}", activity.DisplayName);
+            }
+        }
+        return ExportResult.Success;
+    }
+
+    /// <summary>
+    /// Converts an <see cref="Activity"/> to a <see cref="SpanData"/> record.
+    /// Sets <see cref="SpanData.ParentSpanId"/> to <see langword="null"/> when
+    /// <c>activity.ParentSpanId == default(<see cref="ActivitySpanId"/>)</c> (root span).
+    /// Extracts the <c>agent.conversation_id</c> tag into <see cref="SpanData.ConversationId"/>.
+    /// </summary>
+    internal static SpanData MapToSpanData(Activity activity)
+    {
+        var parentSpanId = activity.ParentSpanId == default(ActivitySpanId)
+            ? null
+            : activity.ParentSpanId.ToHexString();
+
+        var conversationId = activity.GetTagItem("agent.conversation_id") as string;
+
+        var status = activity.Status switch
+        {
+            ActivityStatusCode.Ok => "ok",
+            ActivityStatusCode.Error => "error",
+            _ => "unset"
+        };
+
+        var kind = activity.Kind switch
+        {
+            ActivityKind.Client => "client",
+            ActivityKind.Server => "server",
+            _ => "internal"
+        };
+
+        var tags = activity.Tags
+            .Where(t => t.Value is not null)
+            .ToDictionary(t => t.Key, t => t.Value!)
+            as IReadOnlyDictionary<string, string>
+            ?? new Dictionary<string, string>();
+
+        return new SpanData(
+            Name: activity.DisplayName,
+            TraceId: activity.TraceId.ToHexString(),
+            SpanId: activity.SpanId.ToHexString(),
+            ParentSpanId: parentSpanId,
+            ConversationId: conversationId,
+            StartTime: activity.StartTimeUtc,
+            DurationMs: activity.Duration.TotalMilliseconds,
+            Status: status,
+            StatusDescription: activity.StatusDescription,
+            Kind: kind,
+            SourceName: activity.Source.Name,
+            Tags: tags
+        );
+    }
+
+    /// <summary>
+    /// Starts the background drain loop. Reads spans from the channel and broadcasts via SignalR.
+    /// Uses <c>await Task.WhenAll</c> per span to preserve ordering and propagate exceptions.
+    /// </summary>
+    public Task StartAsync(CancellationToken cancellationToken)
+    {
+        _drainTask = DrainAsync(cancellationToken);
+        return Task.CompletedTask;
+    }
+
+    /// <summary>
+    /// Signals the drain loop to stop by completing the channel writer, then awaits the drain task.
+    /// Safe to call multiple times.
+    /// </summary>
+    public async Task StopAsync(CancellationToken cancellationToken)
+    {
+        _channel.Writer.TryComplete();
+        await _drainTask;
+    }
+
+    private async Task DrainAsync(CancellationToken ct)
+    {
+        await foreach (var span in _channel.Reader.ReadAllAsync(ct))
+        {
+            var tasks = new List<Task>();
+
+            if (span.ConversationId is not null)
+            {
+                tasks.Add(_hubContext.Clients
+                    .Group($"conversation:{span.ConversationId}")
+                    .SendAsync("SpanReceived", span, ct));
+            }
+
+            tasks.Add(_hubContext.Clients
+                .Group("global-traces")
+                .SendAsync("SpanReceived", span, ct));
+
+            await Task.WhenAll(tasks);
+        }
+    }
+}
diff --git a/src/Content/Presentation/Presentation.AgentHub/Telemetry/SpanData.cs b/src/Content/Presentation/Presentation.AgentHub/Telemetry/SpanData.cs
new file mode 100644
index 0000000..4564346
--- /dev/null
+++ b/src/Content/Presentation/Presentation.AgentHub/Telemetry/SpanData.cs
@@ -0,0 +1,32 @@
+namespace Presentation.AgentHub.Telemetry;
+
+/// <summary>
+/// Immutable snapshot of an OpenTelemetry span, serialized over SignalR to connected WebUI clients.
+/// Field names map directly to the TypeScript <c>SpanData</c> interface in the WebUI project.
+/// </summary>
+/// <param name="Name">Display name of the span (Activity.DisplayName).</param>
+/// <param name="TraceId">Hex-encoded trace ID.</param>
+/// <param name="SpanId">Hex-encoded span ID.</param>
+/// <param name="ParentSpanId">Hex-encoded parent span ID, or <see langword="null"/> for root spans.</param>
+/// <param name="ConversationId">Value of the <c>agent.conversation_id</c> activity tag; <see langword="null"/> for non-agent spans.</param>
+/// <param name="StartTime">UTC start time of the span.</param>
+/// <param name="DurationMs">Duration in milliseconds.</param>
+/// <param name="Status">Normalized status string: <c>"unset"</c>, <c>"ok"</c>, or <c>"error"</c>.</param>
+/// <param name="StatusDescription">Optional status description set by the instrumentation library.</param>
+/// <param name="Kind">Normalized kind string: <c>"internal"</c>, <c>"client"</c>, or <c>"server"</c>.</param>
+/// <param name="SourceName">Name of the <see cref="System.Diagnostics.ActivitySource"/> that produced the span.</param>
+/// <param name="Tags">All string-valued tags attached to the span.</param>
+public record SpanData(
+    string Name,
+    string TraceId,
+    string SpanId,
+    string? ParentSpanId,
+    string? ConversationId,
+    DateTimeOffset StartTime,
+    double DurationMs,
+    string Status,
+    string? StatusDescription,
+    string Kind,
+    string SourceName,
+    IReadOnlyDictionary<string, string> Tags
+);
diff --git a/src/Content/Tests/Presentation.AgentHub.Tests/Telemetry/SignalRSpanExporterTests.cs b/src/Content/Tests/Presentation.AgentHub.Tests/Telemetry/SignalRSpanExporterTests.cs
new file mode 100644
index 0000000..aad1ba1
--- /dev/null
+++ b/src/Content/Tests/Presentation.AgentHub.Tests/Telemetry/SignalRSpanExporterTests.cs
@@ -0,0 +1,243 @@
+using System.Diagnostics;
+using FluentAssertions;
+using Microsoft.AspNetCore.SignalR;
+using Microsoft.Extensions.Logging;
+using Moq;
+using OpenTelemetry;
+using Presentation.AgentHub.Hubs;
+using Presentation.AgentHub.Telemetry;
+using Xunit;
+
+namespace Presentation.AgentHub.Tests.Telemetry;
+
+/// <summary>
+/// Unit tests for <see cref="SignalRSpanExporter"/>.
+/// Tests the bounded channel backpressure, span mapping, drain loop routing,
+/// and hosted service lifecycle.
+/// </summary>
+public sealed class SignalRSpanExporterTests : IDisposable
+{
+    // SimpleExportProcessor<T> is abstract to prevent direct instantiation;
+    // a trivial subclass satisfies all abstract members via inheritance.
+    private sealed class TestExportProcessor : SimpleExportProcessor<Activity>
+    {
+        public TestExportProcessor(BaseExporter<Activity> exporter) : base(exporter) { }
+    }
+
+    private readonly Mock<IClientProxy> _mockClientProxy;
+    private readonly Mock<IHubClients> _mockClients;
+    private readonly Mock<IHubContext<AgentTelemetryHub>> _mockHubContext;
+    private readonly Mock<ILogger<SignalRSpanExporter>> _mockLogger;
+    private readonly SignalRSpanExporter _exporter;
+    private readonly BaseProcessor<Activity> _processor;
+
+    public SignalRSpanExporterTests()
+    {
+        _mockClientProxy = new Mock<IClientProxy>();
+        _mockClientProxy
+            .Setup(c => c.SendCoreAsync(
+                It.IsAny<string>(),
+                It.IsAny<object?[]>(),
+                It.IsAny<CancellationToken>()))
+            .Returns(Task.CompletedTask);
+
+        _mockClients = new Mock<IHubClients>();
+        _mockClients
+            .Setup(c => c.Group(It.IsAny<string>()))
+            .Returns(_mockClientProxy.Object);
+
+        _mockHubContext = new Mock<IHubContext<AgentTelemetryHub>>();
+        _mockHubContext.Setup(h => h.Clients).Returns(_mockClients.Object);
+
+        _mockLogger = new Mock<ILogger<SignalRSpanExporter>>();
+
+        _exporter = new SignalRSpanExporter(_mockHubContext.Object, _mockLogger.Object);
+        _processor = new TestExportProcessor(_exporter);
+    }
+
+    public void Dispose()
+    {
+        // TryComplete is idempotent — safe even if StopAsync was already called in the test
+        _exporter.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
+        _exporter.Dispose();
+        _processor.Dispose();
+    }
+
+    // ── Export: Channel backpressure ─────────────────────────────────────────
+
+    [Fact]
+    public void Export_ChannelFull_DoesNotBlock()
+    {
+        // Arrange: fill channel to capacity without a drain loop running
+        for (var i = 0; i < 1000; i++)
+        {
+            var fill = new Activity($"fill-{i}").Start()!;
+            _processor.OnEnd(fill);
+            fill.Dispose();
+        }
+
+        // Act: export one more span and measure elapsed time
+        var overflow = new Activity("overflow").Start()!;
+        var sw = System.Diagnostics.Stopwatch.StartNew();
+        _processor.OnEnd(overflow);
+        sw.Stop();
+        overflow.Dispose();
+
+        // Assert: TryWrite with DropOldest returns immediately — well under 1ms
+        // (100ms threshold for CI headroom)
+        sw.ElapsedMilliseconds.Should().BeLessThan(100);
+    }
+
+    [Fact]
+    public void Export_ChannelFull_LogsWarning()
+    {
+        // Arrange: fill channel to capacity
+        for (var i = 0; i < 1000; i++)
+        {
+            var fill = new Activity($"fill-{i}").Start()!;
+            _processor.OnEnd(fill);
+            fill.Dispose();
+        }
+
+        // Act: export one more (wasFull = true → warning should fire)
+        var overflow = new Activity("overflow").Start()!;
+        _processor.OnEnd(overflow);
+        overflow.Dispose();
+
+        // Assert: a Warning log containing "dropped" or "full" was emitted
+        _mockLogger.Verify(
+            x => x.Log(
+                LogLevel.Warning,
+                It.IsAny<EventId>(),
+                It.Is<It.IsAnyType>((v, _) =>
+                    v.ToString()!.Contains("dropped", StringComparison.OrdinalIgnoreCase) ||
+                    v.ToString()!.Contains("full", StringComparison.OrdinalIgnoreCase)),
+                It.IsAny<Exception?>(),
+                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
+            Times.AtLeastOnce());
+    }
+
+    // ── MapToSpanData: Mapping correctness ───────────────────────────────────
+
+    [Fact]
+    public void MapToSpanData_RootSpan_SetsParentSpanIdNull()
+    {
+        // Arrange: activity with no parent (ParentSpanId == default)
+        using var activity = new Activity("root-span").Start()!;
+
+        // Act
+        var result = SignalRSpanExporter.MapToSpanData(activity);
+
+        // Assert
+        result.ParentSpanId.Should().BeNull();
+    }
+
+    [Fact]
+    public void MapToSpanData_ChildSpan_SetsParentSpanId()
+    {
+        // Arrange: activity with an explicit parent
+        var parentTraceId = ActivityTraceId.CreateRandom();
+        var parentSpanId = ActivitySpanId.CreateRandom();
+        var activity = new Activity("child-span");
+        activity.SetParentId(parentTraceId, parentSpanId);
+        activity.Start();
+
+        // Act
+        var result = SignalRSpanExporter.MapToSpanData(activity);
+        activity.Stop();
+
+        // Assert
+        result.ParentSpanId.Should().Be(parentSpanId.ToHexString());
+    }
+
+    [Fact]
+    public void MapToSpanData_WithConversationIdTag_ExtractsConversationId()
+    {
+        // Arrange
+        using var activity = new Activity("agent-span").Start()!;
+        activity.SetTag("agent.conversation_id", "conv-abc");
+
+        // Act
+        var result = SignalRSpanExporter.MapToSpanData(activity);
+
+        // Assert
+        result.ConversationId.Should().Be("conv-abc");
+    }
+
+    [Fact]
+    public void MapToSpanData_WithoutConversationIdTag_SetsConversationIdNull()
+    {
+        // Arrange: activity with no agent.conversation_id tag
+        using var activity = new Activity("infra-span").Start()!;
+
+        // Act
+        var result = SignalRSpanExporter.MapToSpanData(activity);
+
+        // Assert
+        result.ConversationId.Should().BeNull();
+    }
+
+    // ── Drain loop: SignalR group routing ────────────────────────────────────
+
+    [Fact]
+    public async Task DrainLoop_SpanWithConversationId_SendsToConversationGroup()
+    {
+        // Arrange
+        await _exporter.StartAsync(CancellationToken.None);
+
+        var activity = new Activity("agent-span");
+        activity.SetTag("agent.conversation_id", "conv-1");
+        activity.Start();
+        activity.Stop();
+        _processor.OnEnd(activity);
+
+        // Act: allow drain loop one processing cycle
+        await Task.Delay(150);
+        await _exporter.StopAsync(CancellationToken.None);
+
+        // Assert: conversation group was targeted
+        _mockClients.Verify(c => c.Group("conversation:conv-1"), Times.Once);
+    }
+
+    [Fact]
+    public async Task DrainLoop_AllSpans_AlwaysSentToGlobalTraces()
+    {
+        // Arrange
+        await _exporter.StartAsync(CancellationToken.None);
+
+        // Span WITH conversation ID
+        var withConv = new Activity("conv-span");
+        withConv.SetTag("agent.conversation_id", "conv-x");
+        withConv.Start();
+        withConv.Stop();
+        _processor.OnEnd(withConv);
+
+        // Span WITHOUT conversation ID
+        var noConv = new Activity("infra-span").Start()!;
+        noConv.Stop();
+        _processor.OnEnd(noConv);
+
+        // Act: allow drain loop to process both
+        await Task.Delay(150);
+        await _exporter.StopAsync(CancellationToken.None);
+
+        // Assert: global-traces group was targeted for both spans
+        _mockClients.Verify(c => c.Group("global-traces"), Times.AtLeast(2));
+    }
+
+    [Fact]
+    public async Task StopAsync_CompletesChannelAndDrainLoop_CompletesWithinTimeout()
+    {
+        // Arrange
+        await _exporter.StartAsync(CancellationToken.None);
+
+        // Act
+        var stopTask = _exporter.StopAsync(CancellationToken.None);
+        var completed = await Task.WhenAny(stopTask, Task.Delay(500));
+
+        // Assert: drain loop exited without timeout
+        completed.Should().Be(stopTask);
+        var exception = await Record.ExceptionAsync(() => stopTask);
+        exception.Should().BeNull();
+    }
+}
