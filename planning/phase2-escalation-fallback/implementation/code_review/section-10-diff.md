diff --git a/src/Content/Infrastructure/Infrastructure.AI/Escalation/CompositeEscalationNotifier.cs b/src/Content/Infrastructure/Infrastructure.AI/Escalation/CompositeEscalationNotifier.cs
new file mode 100644
index 0000000..fb996be
--- /dev/null
+++ b/src/Content/Infrastructure/Infrastructure.AI/Escalation/CompositeEscalationNotifier.cs
@@ -0,0 +1,74 @@
+using Application.AI.Common.Interfaces.Escalation;
+using Domain.AI.Escalation;
+using Microsoft.Extensions.Logging;
+
+namespace Infrastructure.AI.Escalation;
+
+/// <summary>
+/// Fans out escalation notifications to all registered <see cref="IEscalationNotificationChannel"/> instances.
+/// Individual channel failures are caught and logged without blocking other channels.
+/// </summary>
+/// <remarks>
+/// Registered as the single <see cref="IEscalationNotifier"/> implementation.
+/// To add a new delivery channel, implement <see cref="IEscalationNotificationChannel"/>
+/// and register it in DI — the composite discovers channels automatically.
+/// </remarks>
+public sealed class CompositeEscalationNotifier : IEscalationNotifier
+{
+    private readonly IReadOnlyList<IEscalationNotificationChannel> _channels;
+    private readonly ILogger<CompositeEscalationNotifier> _logger;
+
+    /// <summary>
+    /// Initializes a new instance of the <see cref="CompositeEscalationNotifier"/> class.
+    /// </summary>
+    /// <param name="channels">All registered notification channels discovered via DI.</param>
+    /// <param name="logger">Logger for recording channel failures.</param>
+    public CompositeEscalationNotifier(
+        IEnumerable<IEscalationNotificationChannel> channels,
+        ILogger<CompositeEscalationNotifier> logger)
+    {
+        _channels = channels.ToList();
+        _logger = logger;
+    }
+
+    /// <inheritdoc />
+    public Task NotifyEscalationRequestedAsync(EscalationRequest request, CancellationToken ct)
+    {
+        return FanOutAsync(
+            channel => channel.NotifyEscalationRequestedAsync(request, ct));
+    }
+
+    /// <inheritdoc />
+    public Task NotifyEscalationResolvedAsync(EscalationOutcome outcome, CancellationToken ct)
+    {
+        return FanOutAsync(
+            channel => channel.NotifyEscalationResolvedAsync(outcome, ct));
+    }
+
+    /// <inheritdoc />
+    public Task NotifyEscalationExpiringAsync(EscalationRequest request, TimeSpan remaining, CancellationToken ct)
+    {
+        return FanOutAsync(
+            channel => channel.NotifyEscalationExpiringAsync(request, remaining, ct));
+    }
+
+    private Task FanOutAsync(Func<IEscalationNotificationChannel, Task> action)
+    {
+        var tasks = _channels.Select(channel => SafeNotifyAsync(action, channel));
+        return Task.WhenAll(tasks);
+    }
+
+    private async Task SafeNotifyAsync(
+        Func<IEscalationNotificationChannel, Task> action,
+        IEscalationNotificationChannel channel)
+    {
+        try
+        {
+            await action(channel);
+        }
+        catch (Exception ex)
+        {
+            _logger.LogWarning(ex, "Notification channel {Channel} failed", channel.GetType().Name);
+        }
+    }
+}
diff --git a/src/Content/Infrastructure/Infrastructure.AI/Escalation/NoOpSlackNotifier.cs b/src/Content/Infrastructure/Infrastructure.AI/Escalation/NoOpSlackNotifier.cs
new file mode 100644
index 0000000..c921788
--- /dev/null
+++ b/src/Content/Infrastructure/Infrastructure.AI/Escalation/NoOpSlackNotifier.cs
@@ -0,0 +1,49 @@
+using Application.AI.Common.Interfaces.Escalation;
+using Domain.AI.Escalation;
+using Microsoft.Extensions.Logging;
+
+namespace Infrastructure.AI.Escalation;
+
+/// <summary>
+/// No-op Slack notification channel. Logs escalation events at Debug level
+/// without delivering them. Replace with a real Slack SDK adapter for production use.
+/// </summary>
+/// <remarks>
+/// Registered as an <see cref="IEscalationNotificationChannel"/> entry in DI.
+/// The <see cref="CompositeEscalationNotifier"/> automatically discovers and
+/// includes this channel in fan-out notifications.
+/// </remarks>
+public sealed class NoOpSlackNotifier : IEscalationNotificationChannel
+{
+    private readonly ILogger<NoOpSlackNotifier> _logger;
+
+    /// <summary>
+    /// Initializes a new instance of the <see cref="NoOpSlackNotifier"/> class.
+    /// </summary>
+    /// <param name="logger">Logger for recording no-op notification events.</param>
+    public NoOpSlackNotifier(ILogger<NoOpSlackNotifier> logger)
+    {
+        _logger = logger;
+    }
+
+    /// <inheritdoc />
+    public Task NotifyEscalationRequestedAsync(EscalationRequest request, CancellationToken ct)
+    {
+        _logger.LogDebug("Slack: would notify escalation requested for {EscalationId}", request.EscalationId);
+        return Task.CompletedTask;
+    }
+
+    /// <inheritdoc />
+    public Task NotifyEscalationResolvedAsync(EscalationOutcome outcome, CancellationToken ct)
+    {
+        _logger.LogDebug("Slack: would notify escalation resolved for {EscalationId}", outcome.EscalationId);
+        return Task.CompletedTask;
+    }
+
+    /// <inheritdoc />
+    public Task NotifyEscalationExpiringAsync(EscalationRequest request, TimeSpan remaining, CancellationToken ct)
+    {
+        _logger.LogDebug("Slack: would notify escalation expiring for {EscalationId} ({Remaining} remaining)", request.EscalationId, remaining);
+        return Task.CompletedTask;
+    }
+}
diff --git a/src/Content/Infrastructure/Infrastructure.AI/Escalation/NoOpTeamsNotifier.cs b/src/Content/Infrastructure/Infrastructure.AI/Escalation/NoOpTeamsNotifier.cs
new file mode 100644
index 0000000..83b0266
--- /dev/null
+++ b/src/Content/Infrastructure/Infrastructure.AI/Escalation/NoOpTeamsNotifier.cs
@@ -0,0 +1,50 @@
+using Application.AI.Common.Interfaces.Escalation;
+using Domain.AI.Escalation;
+using Microsoft.Extensions.Logging;
+
+namespace Infrastructure.AI.Escalation;
+
+/// <summary>
+/// No-op Microsoft Teams notification channel. Logs escalation events at Debug level
+/// without delivering them. Replace with a real Teams webhook or Graph API adapter
+/// for production use.
+/// </summary>
+/// <remarks>
+/// Registered as an <see cref="IEscalationNotificationChannel"/> entry in DI.
+/// The <see cref="CompositeEscalationNotifier"/> automatically discovers and
+/// includes this channel in fan-out notifications.
+/// </remarks>
+public sealed class NoOpTeamsNotifier : IEscalationNotificationChannel
+{
+    private readonly ILogger<NoOpTeamsNotifier> _logger;
+
+    /// <summary>
+    /// Initializes a new instance of the <see cref="NoOpTeamsNotifier"/> class.
+    /// </summary>
+    /// <param name="logger">Logger for recording no-op notification events.</param>
+    public NoOpTeamsNotifier(ILogger<NoOpTeamsNotifier> logger)
+    {
+        _logger = logger;
+    }
+
+    /// <inheritdoc />
+    public Task NotifyEscalationRequestedAsync(EscalationRequest request, CancellationToken ct)
+    {
+        _logger.LogDebug("Teams: would notify escalation requested for {EscalationId}", request.EscalationId);
+        return Task.CompletedTask;
+    }
+
+    /// <inheritdoc />
+    public Task NotifyEscalationResolvedAsync(EscalationOutcome outcome, CancellationToken ct)
+    {
+        _logger.LogDebug("Teams: would notify escalation resolved for {EscalationId}", outcome.EscalationId);
+        return Task.CompletedTask;
+    }
+
+    /// <inheritdoc />
+    public Task NotifyEscalationExpiringAsync(EscalationRequest request, TimeSpan remaining, CancellationToken ct)
+    {
+        _logger.LogDebug("Teams: would notify escalation expiring for {EscalationId} ({Remaining} remaining)", request.EscalationId, remaining);
+        return Task.CompletedTask;
+    }
+}
diff --git a/src/Content/Tests/Infrastructure.AI.Tests/Escalation/CompositeEscalationNotifierTests.cs b/src/Content/Tests/Infrastructure.AI.Tests/Escalation/CompositeEscalationNotifierTests.cs
new file mode 100644
index 0000000..c2f9ba8
--- /dev/null
+++ b/src/Content/Tests/Infrastructure.AI.Tests/Escalation/CompositeEscalationNotifierTests.cs
@@ -0,0 +1,173 @@
+using Application.AI.Common.Interfaces.Escalation;
+using Domain.AI.Escalation;
+using Domain.AI.Governance;
+using FluentAssertions;
+using Infrastructure.AI.Escalation;
+using Microsoft.Extensions.Logging;
+using Moq;
+using Xunit;
+
+namespace Infrastructure.AI.Tests.Escalation;
+
+/// <summary>
+/// Tests for <see cref="CompositeEscalationNotifier"/> fan-out behavior.
+/// Verifies that notifications reach all registered channels and that
+/// individual channel failures do not block other channels.
+/// </summary>
+public sealed class CompositeEscalationNotifierTests
+{
+    private readonly Mock<ILogger<CompositeEscalationNotifier>> _logger = new();
+
+    [Fact]
+    public async Task NotifyEscalationRequestedAsync_FansOutToAllChannels()
+    {
+        var channels = CreateMockChannels(3);
+        var sut = CreateSut(channels);
+        var request = CreateTestRequest();
+
+        await sut.NotifyEscalationRequestedAsync(request, CancellationToken.None);
+
+        foreach (var channel in channels)
+        {
+            channel.Verify(
+                c => c.NotifyEscalationRequestedAsync(request, It.IsAny<CancellationToken>()),
+                Times.Once);
+        }
+    }
+
+    [Fact]
+    public async Task NotifyEscalationResolvedAsync_FansOutToAllChannels()
+    {
+        var channels = CreateMockChannels(2);
+        var sut = CreateSut(channels);
+        var outcome = CreateTestOutcome(Guid.NewGuid());
+
+        await sut.NotifyEscalationResolvedAsync(outcome, CancellationToken.None);
+
+        foreach (var channel in channels)
+        {
+            channel.Verify(
+                c => c.NotifyEscalationResolvedAsync(outcome, It.IsAny<CancellationToken>()),
+                Times.Once);
+        }
+    }
+
+    [Fact]
+    public async Task NotifyEscalationExpiringAsync_FansOutToAllChannels()
+    {
+        var channels = CreateMockChannels(2);
+        var sut = CreateSut(channels);
+        var request = CreateTestRequest();
+        var remaining = TimeSpan.FromMinutes(2);
+
+        await sut.NotifyEscalationExpiringAsync(request, remaining, CancellationToken.None);
+
+        foreach (var channel in channels)
+        {
+            channel.Verify(
+                c => c.NotifyEscalationExpiringAsync(request, remaining, It.IsAny<CancellationToken>()),
+                Times.Once);
+        }
+    }
+
+    [Fact]
+    public async Task NotifyEscalationRequestedAsync_ChannelFailure_DoesNotBlockOthers()
+    {
+        var channels = CreateMockChannels(3);
+        channels[1]
+            .Setup(c => c.NotifyEscalationRequestedAsync(It.IsAny<EscalationRequest>(), It.IsAny<CancellationToken>()))
+            .ThrowsAsync(new InvalidOperationException("Slack is down"));
+
+        var sut = CreateSut(channels);
+        var request = CreateTestRequest();
+
+        await sut.NotifyEscalationRequestedAsync(request, CancellationToken.None);
+
+        channels[0].Verify(
+            c => c.NotifyEscalationRequestedAsync(request, It.IsAny<CancellationToken>()),
+            Times.Once);
+        channels[2].Verify(
+            c => c.NotifyEscalationRequestedAsync(request, It.IsAny<CancellationToken>()),
+            Times.Once);
+    }
+
+    [Fact]
+    public async Task NotifyEscalationRequestedAsync_ChannelFailure_LogsWarning()
+    {
+        var channels = CreateMockChannels(1);
+        channels[0]
+            .Setup(c => c.NotifyEscalationRequestedAsync(It.IsAny<EscalationRequest>(), It.IsAny<CancellationToken>()))
+            .ThrowsAsync(new InvalidOperationException("Channel down"));
+
+        var sut = CreateSut(channels);
+
+        await sut.NotifyEscalationRequestedAsync(CreateTestRequest(), CancellationToken.None);
+
+        _logger.Verify(
+            x => x.Log(
+                LogLevel.Warning,
+                It.IsAny<EventId>(),
+                It.Is<It.IsAnyType>((v, t) => true),
+                It.IsAny<Exception>(),
+                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
+            Times.Once);
+    }
+
+    [Fact]
+    public async Task NoChannelsRegistered_CompletesSuccessfully()
+    {
+        var sut = CreateSut([]);
+        var request = CreateTestRequest();
+        var outcome = CreateTestOutcome(request.EscalationId);
+
+        var act = async () =>
+        {
+            await sut.NotifyEscalationRequestedAsync(request, CancellationToken.None);
+            await sut.NotifyEscalationResolvedAsync(outcome, CancellationToken.None);
+            await sut.NotifyEscalationExpiringAsync(request, TimeSpan.FromMinutes(1), CancellationToken.None);
+        };
+
+        await act.Should().NotThrowAsync();
+    }
+
+    private CompositeEscalationNotifier CreateSut(IReadOnlyList<Mock<IEscalationNotificationChannel>> channels)
+    {
+        return new CompositeEscalationNotifier(
+            channels.Select(c => c.Object),
+            _logger.Object);
+    }
+
+    private static List<Mock<IEscalationNotificationChannel>> CreateMockChannels(int count)
+    {
+        return Enumerable.Range(0, count)
+            .Select(_ => new Mock<IEscalationNotificationChannel>())
+            .ToList();
+    }
+
+    private static EscalationRequest CreateTestRequest() => new()
+    {
+        EscalationId = Guid.NewGuid(),
+        AgentId = "test-agent",
+        ToolName = "dangerous_tool",
+        Arguments = new Dictionary<string, string> { ["arg1"] = "value1" },
+        Description = "Test escalation request",
+        RiskLevel = RiskLevel.High,
+        Priority = EscalationPriority.Blocking,
+        Approvers = ["admin@test.com"],
+        RequestedAt = DateTimeOffset.UtcNow
+    };
+
+    private static EscalationOutcome CreateTestOutcome(Guid escalationId) => new()
+    {
+        EscalationId = escalationId,
+        IsApproved = true,
+        Decisions = [new ApproverDecision
+        {
+            ApproverName = "admin@test.com",
+            Approved = true,
+            RespondedAt = DateTimeOffset.UtcNow
+        }],
+        ResolutionType = EscalationResolutionType.Approved,
+        ResolvedAt = DateTimeOffset.UtcNow
+    };
+}
