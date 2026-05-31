using Application.AI.Common.Prompts.Interfaces;
using Application.AI.Common.Prompts.Models;
using Domain.AI.Prompts;
using FluentAssertions;
using Infrastructure.AI.Prompts;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Prompts;

public sealed class CompositePromptUsageRecorderTests
{
    private static PromptDescriptor Desc() => new()
    {
        Name = "p",
        Version = new PromptVersion(1, 0),
        ContentHash = "h",
        Body = "b",
    };

    private static PromptUsageRecord MakeRecord(string traceId)
        => new()
        {
            Descriptor = Desc(),
            TraceId = traceId,
            RecordedAtUtc = DateTimeOffset.UtcNow,
        };

    private static Mock<IPromptUsageRecorder> Recorder(string traceId)
    {
        var m = new Mock<IPromptUsageRecorder>();
        m.Setup(r => r.RecordAsync(
                It.IsAny<PromptDescriptor>(),
                It.IsAny<PromptUsageContext>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeRecord(traceId));
        return m;
    }

    [Fact]
    public async Task RecordAsync_fans_out_to_every_inner_recorder()
    {
        var a = Recorder("a");
        var b = Recorder("b");
        var c = Recorder("c");

        var sut = new CompositePromptUsageRecorder(
            [a.Object, b.Object, c.Object],
            NullLogger<CompositePromptUsageRecorder>.Instance);

        await sut.RecordAsync(Desc(), PromptUsageContext.Empty, CancellationToken.None);

        a.Verify(r => r.RecordAsync(It.IsAny<PromptDescriptor>(), It.IsAny<PromptUsageContext>(), It.IsAny<CancellationToken>()), Times.Once);
        b.Verify(r => r.RecordAsync(It.IsAny<PromptDescriptor>(), It.IsAny<PromptUsageContext>(), It.IsAny<CancellationToken>()), Times.Once);
        c.Verify(r => r.RecordAsync(It.IsAny<PromptDescriptor>(), It.IsAny<PromptUsageContext>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RecordAsync_returns_first_inner_recorders_record()
    {
        var a = Recorder("first");
        var b = Recorder("second");

        var sut = new CompositePromptUsageRecorder(
            [a.Object, b.Object],
            NullLogger<CompositePromptUsageRecorder>.Instance);

        var result = await sut.RecordAsync(Desc(), PromptUsageContext.Empty, CancellationToken.None);

        result.TraceId.Should().Be("first");
    }

    [Fact]
    public async Task RecordAsync_continues_after_an_inner_recorder_throws()
    {
        var failing = new Mock<IPromptUsageRecorder>();
        failing.Setup(r => r.RecordAsync(
                It.IsAny<PromptDescriptor>(),
                It.IsAny<PromptUsageContext>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var working = Recorder("ok");

        var sut = new CompositePromptUsageRecorder(
            [failing.Object, working.Object],
            NullLogger<CompositePromptUsageRecorder>.Instance);

        var result = await sut.RecordAsync(Desc(), PromptUsageContext.Empty, CancellationToken.None);

        result.TraceId.Should().Be("ok");
        working.Verify(r => r.RecordAsync(It.IsAny<PromptDescriptor>(), It.IsAny<PromptUsageContext>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RecordAsync_returns_synthetic_record_when_every_inner_throws()
    {
        var a = new Mock<IPromptUsageRecorder>();
        a.Setup(r => r.RecordAsync(
                It.IsAny<PromptDescriptor>(),
                It.IsAny<PromptUsageContext>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("a"));
        var b = new Mock<IPromptUsageRecorder>();
        b.Setup(r => r.RecordAsync(
                It.IsAny<PromptDescriptor>(),
                It.IsAny<PromptUsageContext>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("b"));

        var sut = new CompositePromptUsageRecorder(
            [a.Object, b.Object],
            NullLogger<CompositePromptUsageRecorder>.Instance);

        var result = await sut.RecordAsync(
            Desc(),
            new PromptUsageContext { CaseId = "case-x", MetricKey = "metric-x" },
            CancellationToken.None);

        result.Should().NotBeNull();
        result.CaseId.Should().Be("case-x");
        result.MetricKey.Should().Be("metric-x");
        result.Descriptor.Name.Should().Be("p");
    }

    [Fact]
    public async Task RecordAsync_propagates_OperationCanceledException_immediately()
    {
        var cancelling = new Mock<IPromptUsageRecorder>();
        cancelling.Setup(r => r.RecordAsync(
                It.IsAny<PromptDescriptor>(),
                It.IsAny<PromptUsageContext>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var later = Recorder("later");

        var sut = new CompositePromptUsageRecorder(
            [cancelling.Object, later.Object],
            NullLogger<CompositePromptUsageRecorder>.Instance);

        Func<Task> act = () => sut.RecordAsync(Desc(), PromptUsageContext.Empty, CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();

        // The later recorder must NOT be invoked once cancellation propagates.
        later.Verify(r => r.RecordAsync(It.IsAny<PromptDescriptor>(), It.IsAny<PromptUsageContext>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
