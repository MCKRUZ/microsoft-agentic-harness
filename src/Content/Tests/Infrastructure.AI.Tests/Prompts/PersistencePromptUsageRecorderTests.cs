using Application.AI.Common.Prompts.Interfaces;
using Application.AI.Common.Prompts.Models;
using Domain.AI.Prompts;
using FluentAssertions;
using Infrastructure.AI.Prompts;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Prompts;

public sealed class PersistencePromptUsageRecorderTests
{
    private readonly Mock<IPromptUsageStore> _store = new();
    private readonly PersistencePromptUsageRecorder _sut;

    public PersistencePromptUsageRecorderTests()
    {
        _sut = new PersistencePromptUsageRecorder(
            _store.Object,
            NullLogger<PersistencePromptUsageRecorder>.Instance);
    }

    private static PromptDescriptor Desc() => new()
    {
        Name = "p",
        Version = new PromptVersion(1, 0),
        ContentHash = "h",
        Body = "b",
    };

    [Fact]
    public async Task RecordAsync_appends_to_store_with_context_attributes()
    {
        PromptUsageRecord? captured = null;
        _store.Setup(s => s.AppendAsync(It.IsAny<PromptUsageRecord>(), It.IsAny<CancellationToken>()))
            .Callback<PromptUsageRecord, CancellationToken>((r, _) => captured = r)
            .Returns(Task.CompletedTask);

        var context = new PromptUsageContext { CaseId = "c-1", MetricKey = "m" };

        var result = await _sut.RecordAsync(Desc(), context, CancellationToken.None);

        captured.Should().NotBeNull();
        captured!.CaseId.Should().Be("c-1");
        captured.MetricKey.Should().Be("m");
        captured.Descriptor.Name.Should().Be("p");
        result.Should().BeSameAs(captured);
    }

    [Fact]
    public async Task RecordAsync_swallows_store_failures_to_honor_never_throws_contract()
    {
        _store.Setup(s => s.AppendAsync(It.IsAny<PromptUsageRecord>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("store down"));

        var result = await _sut.RecordAsync(Desc(), PromptUsageContext.Empty, CancellationToken.None);

        result.Should().NotBeNull();
        result.Descriptor.Name.Should().Be("p");
    }

    [Fact]
    public async Task RecordAsync_propagates_OperationCanceledException()
    {
        _store.Setup(s => s.AppendAsync(It.IsAny<PromptUsageRecord>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        Func<Task> act = () => _sut.RecordAsync(Desc(), PromptUsageContext.Empty, CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
