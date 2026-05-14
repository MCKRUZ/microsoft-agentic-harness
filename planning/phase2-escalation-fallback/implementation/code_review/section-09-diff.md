diff --git a/src/Content/Domain/Domain.Common/Config/AI/Governance/EscalationConfig.cs b/src/Content/Domain/Domain.Common/Config/AI/Governance/EscalationConfig.cs
index a3a6309..eda2390 100644
--- a/src/Content/Domain/Domain.Common/Config/AI/Governance/EscalationConfig.cs
+++ b/src/Content/Domain/Domain.Common/Config/AI/Governance/EscalationConfig.cs
@@ -13,6 +13,7 @@ namespace Domain.Common.Config.AI.Governance;
 /// ├── DefaultTimeoutSeconds    — Global escalation timeout
 /// ├── DefaultTimeoutAction     — Deny / DenyAndEscalate / Approve / Escalate
 /// ├── DefaultApprovalStrategy  — AnyOf / AllOf / Quorum
+/// ├── AuditStoragePath          — Directory for JSONL audit log
 /// └── PriorityLevels{}         — Per-priority overrides keyed by EscalationPriority name
 ///     ├── TimeoutSeconds       — Override timeout for this level
 ///     ├── Async                — Non-blocking mode (informational)
@@ -53,4 +54,10 @@ public class EscalationConfig
     /// ("Informational", "Blocking", "Critical").
     /// </summary>
     public Dictionary<string, EscalationPriorityConfig> PriorityLevels { get; set; } = new();
+
+    /// <summary>
+    /// Directory path for the JSONL escalation audit store.
+    /// Relative paths are resolved from the application working directory.
+    /// </summary>
+    public string AuditStoragePath { get; set; } = ".agent-sessions/escalations";
 }
diff --git a/src/Content/Infrastructure/Infrastructure.AI/Escalation/JsonlEscalationAuditStore.cs b/src/Content/Infrastructure/Infrastructure.AI/Escalation/JsonlEscalationAuditStore.cs
new file mode 100644
index 0000000..83dc0b5
--- /dev/null
+++ b/src/Content/Infrastructure/Infrastructure.AI/Escalation/JsonlEscalationAuditStore.cs
@@ -0,0 +1,191 @@
+using System.Text.Json;
+using System.Text.Json.Serialization;
+using Application.AI.Common.Interfaces.Escalation;
+using Domain.AI.Escalation;
+using Domain.Common.Config;
+using Microsoft.Extensions.Logging;
+using Microsoft.Extensions.Options;
+
+namespace Infrastructure.AI.Escalation;
+
+/// <summary>
+/// Append-only JSONL file store for escalation audit records.
+/// Each line is a serialized <see cref="EscalationAuditRecord"/> with a
+/// <see cref="EscalationAuditRecordType"/> discriminator.
+/// Thread-safe via a single <see cref="SemaphoreSlim"/> for file access.
+/// </summary>
+/// <remarks>
+/// Follows the same pattern as <see cref="Agents.JsonlDelegationStore"/>:
+/// snake_case JSON, enum-as-string, <c>FileShare.ReadWrite</c> for concurrent reads.
+/// The file is created lazily on first write in the configured
+/// <c>EscalationConfig.AuditStoragePath</c> directory.
+/// </remarks>
+public sealed class JsonlEscalationAuditStore : IEscalationAuditStore, IDisposable
+{
+    private static readonly JsonSerializerOptions SerializeOptions = new()
+    {
+        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
+        WriteIndented = false,
+        Converters = { new JsonStringEnumConverter() }
+    };
+
+    private static readonly JsonSerializerOptions DeserializeOptions = new()
+    {
+        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
+        PropertyNameCaseInsensitive = true,
+        Converters = { new JsonStringEnumConverter() }
+    };
+
+    private readonly string _filePath;
+    private readonly ILogger<JsonlEscalationAuditStore> _logger;
+    private readonly SemaphoreSlim _semaphore = new(1, 1);
+
+    /// <summary>
+    /// Initializes a new instance of <see cref="JsonlEscalationAuditStore"/>.
+    /// </summary>
+    /// <param name="config">Application configuration providing the audit storage path.</param>
+    /// <param name="logger">Logger for operational diagnostics.</param>
+    public JsonlEscalationAuditStore(
+        IOptionsMonitor<AppConfig> config,
+        ILogger<JsonlEscalationAuditStore> logger)
+    {
+        _filePath = Path.Combine(
+            config.CurrentValue.AI.Governance.Escalation.AuditStoragePath,
+            "escalations.jsonl");
+        _logger = logger;
+    }
+
+    /// <inheritdoc />
+    public async Task RecordRequestAsync(EscalationRequest request, CancellationToken ct)
+    {
+        ArgumentNullException.ThrowIfNull(request);
+
+        var record = new EscalationAuditRecord
+        {
+            RecordType = EscalationAuditRecordType.Request,
+            EscalationId = request.EscalationId,
+            Timestamp = DateTimeOffset.UtcNow,
+            Payload = JsonSerializer.Serialize(request, SerializeOptions)
+        };
+
+        await AppendRecordAsync(record, ct);
+    }
+
+    /// <inheritdoc />
+    public async Task RecordDecisionAsync(Guid escalationId, ApproverDecision decision, CancellationToken ct)
+    {
+        ArgumentNullException.ThrowIfNull(decision);
+
+        var record = new EscalationAuditRecord
+        {
+            RecordType = EscalationAuditRecordType.Decision,
+            EscalationId = escalationId,
+            Timestamp = DateTimeOffset.UtcNow,
+            Payload = JsonSerializer.Serialize(decision, SerializeOptions)
+        };
+
+        await AppendRecordAsync(record, ct);
+    }
+
+    /// <inheritdoc />
+    public async Task RecordOutcomeAsync(EscalationOutcome outcome, CancellationToken ct)
+    {
+        ArgumentNullException.ThrowIfNull(outcome);
+
+        var record = new EscalationAuditRecord
+        {
+            RecordType = EscalationAuditRecordType.Outcome,
+            EscalationId = outcome.EscalationId,
+            Timestamp = DateTimeOffset.UtcNow,
+            Payload = JsonSerializer.Serialize(outcome, SerializeOptions)
+        };
+
+        await AppendRecordAsync(record, ct);
+    }
+
+    /// <inheritdoc />
+    public async Task<IReadOnlyList<EscalationAuditRecord>> GetHistoryAsync(
+        Guid escalationId,
+        CancellationToken ct)
+    {
+        if (!File.Exists(_filePath))
+            return [];
+
+        var records = new List<EscalationAuditRecord>();
+
+        await _semaphore.WaitAsync(ct);
+        try
+        {
+            await using var stream = new FileStream(
+                _filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
+            using var reader = new StreamReader(stream);
+
+            var lineNumber = 0;
+            while (await reader.ReadLineAsync(ct) is { } line)
+            {
+                lineNumber++;
+
+                if (string.IsNullOrWhiteSpace(line))
+                    continue;
+
+                try
+                {
+                    var record = JsonSerializer.Deserialize<EscalationAuditRecord>(line, DeserializeOptions);
+                    if (record is not null && record.EscalationId == escalationId)
+                        records.Add(record);
+                }
+                catch (JsonException)
+                {
+                    _logger.LogWarning(
+                        "Skipped corrupted audit record at {FilePath}:{LineNumber}",
+                        _filePath, lineNumber);
+                }
+            }
+        }
+        finally
+        {
+            _semaphore.Release();
+        }
+
+        return records.OrderBy(r => r.Timestamp).ToList();
+    }
+
+    /// <inheritdoc cref="IDisposable.Dispose" />
+    public void Dispose()
+    {
+        _semaphore.Dispose();
+    }
+
+    /// <summary>
+    /// Serializes and appends a single audit record as one JSONL line.
+    /// </summary>
+    private async Task AppendRecordAsync(EscalationAuditRecord record, CancellationToken ct)
+    {
+        var line = JsonSerializer.Serialize(record, SerializeOptions) + "\n";
+
+        await _semaphore.WaitAsync(ct);
+        try
+        {
+            EnsureDirectoryExists(_filePath);
+            await File.AppendAllTextAsync(_filePath, line, ct);
+        }
+        finally
+        {
+            _semaphore.Release();
+        }
+
+        _logger.LogDebug(
+            "Appended escalation audit {RecordType} for {EscalationId} to {FilePath}",
+            record.RecordType, record.EscalationId, _filePath);
+    }
+
+    /// <summary>
+    /// Ensures the parent directory for the given file path exists.
+    /// </summary>
+    private static void EnsureDirectoryExists(string filePath)
+    {
+        var dir = Path.GetDirectoryName(filePath);
+        if (dir is not null)
+            Directory.CreateDirectory(dir);
+    }
+}
diff --git a/src/Content/Tests/Infrastructure.AI.Tests/Escalation/JsonlEscalationAuditStoreTests.cs b/src/Content/Tests/Infrastructure.AI.Tests/Escalation/JsonlEscalationAuditStoreTests.cs
new file mode 100644
index 0000000..6c5bd9a
--- /dev/null
+++ b/src/Content/Tests/Infrastructure.AI.Tests/Escalation/JsonlEscalationAuditStoreTests.cs
@@ -0,0 +1,190 @@
+using Domain.AI.Escalation;
+using Domain.Common.Config;
+using Domain.Common.Config.AI;
+using Domain.Common.Config.AI.Governance;
+using FluentAssertions;
+using Infrastructure.AI.Escalation;
+using Microsoft.Extensions.Logging;
+using Microsoft.Extensions.Options;
+using Moq;
+using Xunit;
+
+namespace Infrastructure.AI.Tests.Escalation;
+
+/// <summary>
+/// Tests for <see cref="JsonlEscalationAuditStore"/>.
+/// Validates append-only JSONL semantics, round-trip serialization with RecordType
+/// discriminator, concurrent write safety, and history retrieval by escalation ID.
+/// </summary>
+public sealed class JsonlEscalationAuditStoreTests : IDisposable
+{
+    private readonly string _tempDir = Path.Combine(
+        Path.GetTempPath(), $"audit-store-tests-{Guid.NewGuid():N}");
+
+    private readonly JsonlEscalationAuditStore _store;
+
+    public JsonlEscalationAuditStoreTests()
+    {
+        var config = new AppConfig
+        {
+            AI = new AIConfig
+            {
+                Governance = new GovernanceConfig
+                {
+                    Escalation = new EscalationConfig { AuditStoragePath = _tempDir }
+                }
+            }
+        };
+        var options = Mock.Of<IOptionsMonitor<AppConfig>>(o => o.CurrentValue == config);
+        _store = new JsonlEscalationAuditStore(options, Mock.Of<ILogger<JsonlEscalationAuditStore>>());
+    }
+
+    public void Dispose()
+    {
+        _store.Dispose();
+        if (Directory.Exists(_tempDir))
+            Directory.Delete(_tempDir, recursive: true);
+    }
+
+    private static EscalationRequest BuildRequest(Guid? escalationId = null) => new()
+    {
+        EscalationId = escalationId ?? Guid.NewGuid(),
+        AgentId = "agent-1",
+        ToolName = "delete_file",
+        Arguments = new Dictionary<string, string> { ["path"] = "/tmp/test.txt" },
+        Description = "Delete a temporary file",
+        RiskLevel = RiskLevel.Medium,
+        Priority = EscalationPriority.Blocking,
+        Approvers = ["admin"],
+        RequestedAt = DateTimeOffset.UtcNow
+    };
+
+    private static ApproverDecision BuildDecision(string approver = "admin") => new()
+    {
+        ApproverName = approver,
+        Approved = true,
+        Reason = "Looks safe",
+        RespondedAt = DateTimeOffset.UtcNow
+    };
+
+    private static EscalationOutcome BuildOutcome(Guid escalationId) => new()
+    {
+        EscalationId = escalationId,
+        IsApproved = true,
+        Decisions = [BuildDecision()],
+        ResolutionType = EscalationResolutionType.Approved,
+        ResolvedAt = DateTimeOffset.UtcNow
+    };
+
+    [Fact]
+    public async Task RecordRequestAsync_AppendsToFile()
+    {
+        var request = BuildRequest();
+
+        await _store.RecordRequestAsync(request, CancellationToken.None);
+
+        var history = await _store.GetHistoryAsync(request.EscalationId, CancellationToken.None);
+        history.Should().HaveCount(1);
+        history[0].RecordType.Should().Be(EscalationAuditRecordType.Request);
+        history[0].EscalationId.Should().Be(request.EscalationId);
+    }
+
+    [Fact]
+    public async Task RecordDecisionAsync_AppendsToFile()
+    {
+        var escalationId = Guid.NewGuid();
+        var decision = BuildDecision();
+
+        await _store.RecordDecisionAsync(escalationId, decision, CancellationToken.None);
+
+        var history = await _store.GetHistoryAsync(escalationId, CancellationToken.None);
+        history.Should().HaveCount(1);
+        history[0].RecordType.Should().Be(EscalationAuditRecordType.Decision);
+        history[0].EscalationId.Should().Be(escalationId);
+    }
+
+    [Fact]
+    public async Task RecordOutcomeAsync_AppendsToFile()
+    {
+        var escalationId = Guid.NewGuid();
+        var outcome = BuildOutcome(escalationId);
+
+        await _store.RecordOutcomeAsync(outcome, CancellationToken.None);
+
+        var history = await _store.GetHistoryAsync(escalationId, CancellationToken.None);
+        history.Should().HaveCount(1);
+        history[0].RecordType.Should().Be(EscalationAuditRecordType.Outcome);
+    }
+
+    [Fact]
+    public async Task GetHistoryAsync_ReturnsAllRecordsForEscalation()
+    {
+        var escalationId = Guid.NewGuid();
+        var noiseId = Guid.NewGuid();
+
+        await _store.RecordRequestAsync(BuildRequest(escalationId), CancellationToken.None);
+        await _store.RecordDecisionAsync(escalationId, BuildDecision(), CancellationToken.None);
+        await _store.RecordOutcomeAsync(BuildOutcome(escalationId), CancellationToken.None);
+        await _store.RecordRequestAsync(BuildRequest(noiseId), CancellationToken.None);
+
+        var history = await _store.GetHistoryAsync(escalationId, CancellationToken.None);
+
+        history.Should().HaveCount(3);
+        history.Should().AllSatisfy(r => r.EscalationId.Should().Be(escalationId));
+        history[0].RecordType.Should().Be(EscalationAuditRecordType.Request);
+        history[1].RecordType.Should().Be(EscalationAuditRecordType.Decision);
+        history[2].RecordType.Should().Be(EscalationAuditRecordType.Outcome);
+    }
+
+    [Fact]
+    public async Task GetHistoryAsync_UnknownId_ReturnsEmpty()
+    {
+        await _store.RecordRequestAsync(BuildRequest(), CancellationToken.None);
+
+        var history = await _store.GetHistoryAsync(Guid.NewGuid(), CancellationToken.None);
+
+        history.Should().BeEmpty();
+    }
+
+    [Fact]
+    public async Task ConcurrentWrites_NoCorruption()
+    {
+        var requests = Enumerable.Range(0, 20)
+            .Select(_ => BuildRequest())
+            .ToList();
+
+        await Task.WhenAll(requests.Select(r =>
+            _store.RecordRequestAsync(r, CancellationToken.None)));
+
+        var filePath = Path.Combine(_tempDir, "escalations.jsonl");
+        var lines = await File.ReadAllLinesAsync(filePath);
+        lines.Where(l => !string.IsNullOrWhiteSpace(l)).Should().HaveCount(20);
+
+        foreach (var request in requests)
+        {
+            var history = await _store.GetHistoryAsync(request.EscalationId, CancellationToken.None);
+            history.Should().HaveCount(1);
+        }
+    }
+
+    [Fact]
+    public async Task RecordType_Discriminator_DeserializesCorrectly()
+    {
+        var escalationId = Guid.NewGuid();
+
+        await _store.RecordRequestAsync(BuildRequest(escalationId), CancellationToken.None);
+        await _store.RecordDecisionAsync(escalationId, BuildDecision(), CancellationToken.None);
+        await _store.RecordOutcomeAsync(BuildOutcome(escalationId), CancellationToken.None);
+
+        var history = await _store.GetHistoryAsync(escalationId, CancellationToken.None);
+
+        history[0].RecordType.Should().Be(EscalationAuditRecordType.Request);
+        history[0].Payload.Should().Contain("delete_file");
+
+        history[1].RecordType.Should().Be(EscalationAuditRecordType.Decision);
+        history[1].Payload.Should().Contain("admin");
+
+        history[2].RecordType.Should().Be(EscalationAuditRecordType.Outcome);
+        history[2].Payload.Should().Contain("Approved");
+    }
+}
