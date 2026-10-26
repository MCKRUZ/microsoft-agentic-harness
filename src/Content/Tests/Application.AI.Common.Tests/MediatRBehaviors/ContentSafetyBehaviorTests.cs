using Application.AI.Common.Exceptions;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.MediatR;
using Application.AI.Common.MediatRBehaviors;
using Domain.AI.Models;
using Domain.AI.Telemetry.Conventions;
using Domain.Common;
using Domain.Common.Config;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.Options;
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

    [Fact]
    public async Task Handle_ContentCannotBeScreened_RefusesByDefaultAndRecordsUnscreened()
    {
        // A screen that lets everything through while it is down is no screen; and an outage is not a verdict on the
        // text, so it is recorded as unscreened, never as a pass or a block.
        _safetyService
            .Setup(s => s.ScreenAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ContentSafetyUnavailableException());
        var handlerCalled = false;

        var act = async () => await CreateBehavior<SessionScreenableRequest, string>().Handle(
            new SessionScreenableRequest("content", _sessionId),
            () => { handlerCalled = true; return Task.FromResult("ok"); },
            CancellationToken.None);

        await act.Should().ThrowAsync<ContentSafetyUnavailableException>();
        handlerCalled.Should().BeFalse();
        _observabilityStore.Verify(o => o.RecordSafetyEventAsync(
            _sessionId, "prompt", SafetyConventions.OutcomeValues.Unscreened, null, null, "pipeline_behavior",
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_ContentCannotBeScreenedAndTheDeploymentAllowsIt_ContinuesAndRecordsUnscreened()
    {
        _appConfig.AI.ContentSafety.BlockWhenUnavailable = false;
        _safetyService
            .Setup(s => s.ScreenAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ContentSafetyUnavailableException());

        var result = await CreateBehavior<SessionScreenableRequest, string>().Handle(
            new SessionScreenableRequest("content", _sessionId),
            () => Task.FromResult("handled"),
            CancellationToken.None);

        result.Should().Be("handled");
        _observabilityStore.Verify(o => o.RecordSafetyEventAsync(
            _sessionId, "prompt", SafetyConventions.OutcomeValues.Unscreened, null, null, "pipeline_behavior",
            It.IsAny<CancellationToken>()), Times.Once, "letting unscreened text through must not read as a pass");
    }

    private readonly Mock<IObservabilityStore> _observabilityStore = new();
    private readonly AppConfig _appConfig = new();
    private readonly Guid _sessionId = Guid.NewGuid();

    private ContentSafetyBehavior<TRequest, TResponse> CreateBehavior<TRequest, TResponse>()
        where TRequest : notnull
    {
        return new ContentSafetyBehavior<TRequest, TResponse>(
            _safetyService.Object,
            _observabilityStore.Object,
            Mock.Of<IOptionsMonitor<AppConfig>>(m => m.CurrentValue == _appConfig));
    }

    // Test request types
    public record NonScreenableRequest : IRequest<string>;

    public record SessionScreenableRequest(string Content, Guid ObservabilitySessionId)
        : IRequest<string>, IContentScreenable, IHasObservabilitySession
    {
        public string ContentToScreen => Content;
        public ContentScreeningTarget ScreeningTarget => ContentScreeningTarget.Input;
    }

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
