using Application.AI.Common.Interfaces.MediatR;
using Application.AI.Common.MediatRBehaviors;
using Application.AI.Common.Prompts.Interfaces;
using Application.AI.Common.Prompts.Models;
using Domain.AI.Prompts;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.MediatRBehaviors;

public sealed class PromptUsageTrackingBehaviorTests
{
    private readonly Mock<IPromptUsageBag> _bag = new();
    private readonly Mock<IPromptUsageRecorder> _recorder = new();

    private static PromptDescriptor Desc(string name = "p") => new()
    {
        Name = name,
        Version = new PromptVersion(1, 0),
        ContentHash = "h",
        Body = "b",
    };

    private sealed record NoMarkerRequest : IRequest<string>;
    private sealed record MarkerRequest : IRequest<string>, IConsumesPrompts
    {
        public IReadOnlyList<string> ExpectedPromptNames { get; init; } = ["p1"];
    }

    [Fact]
    public async Task Non_marker_request_skips_drain_and_record()
    {
        var sut = Create<NoMarkerRequest, string>();

        var result = await sut.Handle(
            new NoMarkerRequest(),
            () => Task.FromResult("ok"),
            CancellationToken.None);

        result.Should().Be("ok");
        _bag.Verify(b => b.Drain(), Times.Never);
        _recorder.Verify(
            r => r.RecordAsync(It.IsAny<PromptDescriptor>(), It.IsAny<PromptUsageContext>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Marker_request_drains_and_records_after_handler_succeeds()
    {
        _recorder
            .Setup(r => r.RecordAsync(It.IsAny<PromptDescriptor>(), It.IsAny<PromptUsageContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PromptDescriptor d, PromptUsageContext c, CancellationToken _) => new PromptUsageRecord
            {
                Descriptor = d,
                RecordedAtUtc = DateTimeOffset.UtcNow,
            });

        _bag.Setup(b => b.Drain()).Returns(new[]
        {
            new PromptUsageBagEntry(Desc("a"), new PromptUsageContext { CaseId = "x" }),
            new PromptUsageBagEntry(Desc("b"), new PromptUsageContext { CaseId = "y" }),
        });

        var sut = Create<MarkerRequest, string>();

        var result = await sut.Handle(
            new MarkerRequest(),
            () => Task.FromResult("ok"),
            CancellationToken.None);

        result.Should().Be("ok");
        _bag.Verify(b => b.Drain(), Times.Once);
        _recorder.Verify(
            r => r.RecordAsync(It.Is<PromptDescriptor>(d => d.Name == "a"), It.Is<PromptUsageContext>(c => c.CaseId == "x"), It.IsAny<CancellationToken>()),
            Times.Once);
        _recorder.Verify(
            r => r.RecordAsync(It.Is<PromptDescriptor>(d => d.Name == "b"), It.Is<PromptUsageContext>(c => c.CaseId == "y"), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Marker_request_with_empty_bag_records_nothing_and_does_not_call_recorder()
    {
        _bag.Setup(b => b.Drain()).Returns([]);

        var sut = Create<MarkerRequest, string>();
        var result = await sut.Handle(new MarkerRequest(), () => Task.FromResult("ok"), CancellationToken.None);

        result.Should().Be("ok");
        _bag.Verify(b => b.Drain(), Times.Once);
        _recorder.Verify(
            r => r.RecordAsync(It.IsAny<PromptDescriptor>(), It.IsAny<PromptUsageContext>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Handler_failure_still_drains_records_then_rethrows_original_exception()
    {
        _recorder
            .Setup(r => r.RecordAsync(It.IsAny<PromptDescriptor>(), It.IsAny<PromptUsageContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PromptUsageRecord
            {
                Descriptor = Desc(),
                RecordedAtUtc = DateTimeOffset.UtcNow,
            });

        _bag.Setup(b => b.Drain()).Returns(new[]
        {
            new PromptUsageBagEntry(Desc("partial"), new PromptUsageContext { CaseId = "before-throw" }),
        });

        var sut = Create<MarkerRequest, string>();

        var thrown = new InvalidOperationException("handler boom");
        Func<Task> act = () => sut.Handle(
            new MarkerRequest(),
            () => throw thrown,
            CancellationToken.None);

        var actual = await act.Should().ThrowAsync<InvalidOperationException>();
        actual.Which.Should().BeSameAs(thrown);

        _bag.Verify(b => b.Drain(), Times.Once);
        _recorder.Verify(
            r => r.RecordAsync(It.Is<PromptDescriptor>(d => d.Name == "partial"), It.IsAny<PromptUsageContext>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task OperationCanceled_propagates_without_drain()
    {
        var sut = Create<MarkerRequest, string>();

        Func<Task> act = () => sut.Handle(
            new MarkerRequest(),
            () => throw new OperationCanceledException(),
            CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();

        _bag.Verify(b => b.Drain(), Times.Never);
        _recorder.VerifyNoOtherCalls();
    }

    private PromptUsageTrackingBehavior<TRequest, TResponse> Create<TRequest, TResponse>()
        where TRequest : notnull
        => new(_bag.Object, _recorder.Object, NullLogger<PromptUsageTrackingBehavior<TRequest, TResponse>>.Instance);
}
