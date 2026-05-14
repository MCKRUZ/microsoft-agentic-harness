using Domain.AI.KnowledgeGraph;
using Domain.AI.KnowledgeGraph.Models;
using FluentAssertions;
using Infrastructure.AI.KnowledgeGraph.Audit;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Infrastructure.AI.KnowledgeGraph.Tests.Audit;

public sealed class StructuredLoggingAuditSinkTests
{
    private readonly Mock<ILogger<StructuredLoggingAuditSink>> _loggerMock = new();
    private readonly StructuredLoggingAuditSink _sink;

    public StructuredLoggingAuditSinkTests()
    {
        _sink = new StructuredLoggingAuditSink(_loggerMock.Object);
    }

    [Fact]
    public async Task EmitAsync_LogsAuditEvent()
    {
        var auditEvent = CreateEvent(MemoryAuditAction.Remember);

        await _sink.EmitAsync(auditEvent);

        _loggerMock.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((o, _) => o.ToString()!.Contains("Remember")),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task EmitBatchAsync_LogsEachEvent()
    {
        var events = new[]
        {
            CreateEvent(MemoryAuditAction.Remember),
            CreateEvent(MemoryAuditAction.Recall)
        };

        await _sink.EmitBatchAsync(events);

        _loggerMock.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Exactly(2));
    }

    private static MemoryAuditEvent CreateEvent(MemoryAuditAction action) => new()
    {
        EventId = Guid.NewGuid().ToString(),
        Action = action,
        ActorId = "test-user",
        Timestamp = DateTimeOffset.UtcNow,
        ScopeId = "test-scope"
    };
}
