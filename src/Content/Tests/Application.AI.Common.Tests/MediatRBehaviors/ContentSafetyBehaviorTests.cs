using Application.AI.Common.Exceptions;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.MediatR;
using Application.AI.Common.MediatRBehaviors;
using Domain.AI.Models;
using Domain.Common;
using FluentAssertions;
using MediatR;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.MediatRBehaviors;

public class ContentSafetyBehaviorTests
{
    private readonly Mock<ITextContentSafetyService> _safetyService;

    public ContentSafetyBehaviorTests()
    {
        _safetyService = new Mock<ITextContentSafetyService>();
    }

    [Fact]
    public async Task Handle_NonScreenableRequest_PassesThrough()
    {
        var behavior = CreateBehavior<NonScreenableRequest, string>();
        var expected = "handler result";
        var handlerCalled = false;

        var result = await behavior.Handle(
            new NonScreenableRequest(),
            () =>
            {
                handlerCalled = true;
                return Task.FromResult(expected);
            },
            CancellationToken.None);

        result.Should().Be(expected);
        handlerCalled.Should().BeTrue();
        _safetyService.Verify(
            s => s.ScreenAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Handle_ScreenableRequest_SafeContent_PassesThrough()
    {
        _safetyService
            .Setup(s => s.ScreenAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContentSafetyResult(false, null, null));

        var behavior = CreateBehavior<ScreenableRequest, string>();
        var expected = "handler result";

        var result = await behavior.Handle(
            new ScreenableRequest("safe content"),
            () => Task.FromResult(expected),
            CancellationToken.None);

        result.Should().Be(expected);
    }

    [Fact]
    public async Task Handle_ScreenableRequest_UnsafeContent_ThrowsContentSafetyException()
    {
        _safetyService
            .Setup(s => s.ScreenAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContentSafetyResult(true, "Contains harmful content", "violence"));

        var behavior = CreateBehavior<ScreenableRequest, string>();
        var handlerCalled = false;

        var act = async () => await behavior.Handle(
            new ScreenableRequest("violent content"),
            () =>
            {
                handlerCalled = true;
                return Task.FromResult("should not reach");
            },
            CancellationToken.None);

        await act.Should().ThrowAsync<ContentSafetyException>();
        handlerCalled.Should().BeFalse();
    }

    [Fact]
    public async Task Handle_ScreenableRequest_UnsafeContent_ScreensCorrectContent()
    {
        _safetyService
            .Setup(s => s.ScreenAsync("specific content to screen", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContentSafetyResult(true, "Blocked", null));

        var behavior = CreateBehavior<ScreenableRequest, string>();

        var act = async () => await behavior.Handle(
            new ScreenableRequest("specific content to screen"),
            () => Task.FromResult("ok"),
            CancellationToken.None);

        await act.Should().ThrowAsync<ContentSafetyException>();
        _safetyService.Verify(
            s => s.ScreenAsync("specific content to screen", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Handle_OutputOnlyScreenable_SkipsInputScreening()
    {
        var behavior = CreateBehavior<OutputOnlyScreenableRequest, string>();
        var expected = "handler result";

        var result = await behavior.Handle(
            new OutputOnlyScreenableRequest("content"),
            () => Task.FromResult(expected),
            CancellationToken.None);

        result.Should().Be(expected);
        _safetyService.Verify(
            s => s.ScreenAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private ContentSafetyBehavior<TRequest, TResponse> CreateBehavior<TRequest, TResponse>()
        where TRequest : notnull
    {
        return new ContentSafetyBehavior<TRequest, TResponse>(
            _safetyService.Object,
            new Mock<IObservabilityStore>().Object);
    }

    // Test request types
    public record NonScreenableRequest : IRequest<string>;

    public record ScreenableRequest(string Content) : IRequest<string>, IContentScreenable
    {
        public string ContentToScreen => Content;
        public ContentScreeningTarget ScreeningTarget => ContentScreeningTarget.Input;
    }

    public record OutputOnlyScreenableRequest(string Content) : IRequest<string>, IContentScreenable
    {
        public string ContentToScreen => Content;
        public ContentScreeningTarget ScreeningTarget => ContentScreeningTarget.Output;
    }
}
