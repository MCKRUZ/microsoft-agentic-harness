using Domain.AI.Models;
using FluentAssertions;
using Infrastructure.AI.Tools;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Infrastructure.AI.Tests.Tools;

/// <summary>
/// Unit tests for <see cref="MediatorDispatchRunner"/> — the shared dispatch wrapper extracted from
/// <c>WorkspaceWriteFileTool</c> and <c>DocumentIngestTool</c> (#428).
/// </summary>
public sealed class MediatorDispatchRunnerTests
{
    [Fact]
    public async Task RunAsync_DispatchThrowsOperationCanceledException_CallerTokenNotCancelled_MapsToFailure()
    {
        // #428 correctness-review follow-up: an OperationCanceledException from an internal timeout
        // unrelated to the caller's own token (e.g. HttpClient's own timeout mid-fetch) must still be
        // mapped to a failed ToolResult, not rethrown — rethrowing unconditionally reopens the exact
        // uncaught-exception gap #428 exists to close.
        var services = new ServiceCollection().AddScoped(_ => (IMediator)new ThrowingOceMediator());
        using var provider = services.BuildServiceProvider();

        var result = await MediatorDispatchRunner.RunAsync(
            provider.GetRequiredService<IServiceScopeFactory>(),
            async (mediator, ct) => { await mediator.Send(new Ping(), ct); return null!; },
            NullLogger.Instance,
            "test_tool",
            failureContext: "n/a",
            cancellationToken: CancellationToken.None);

        result.Success.Should().BeFalse(
            "an OperationCanceledException unrelated to the caller's own token must be mapped to a " +
            "failure, not rethrown");
    }

    [Fact]
    public async Task RunAsync_CallerCancelsOwnToken_Rethrows()
    {
        var services = new ServiceCollection().AddScoped(_ => (IMediator)new ThrowingOceMediator());
        using var provider = services.BuildServiceProvider();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => MediatorDispatchRunner.RunAsync(
            provider.GetRequiredService<IServiceScopeFactory>(),
            async (mediator, ct) => { await mediator.Send(new Ping(), ct); return null!; },
            NullLogger.Instance,
            "test_tool",
            failureContext: "n/a",
            cancellationToken: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>(
            "the caller's own cancellation must still propagate, not be swallowed into a ToolResult.Fail");
    }

    [Fact]
    public async Task RunAsync_DispatchSucceeds_ScopeDisposalThrows_StillReturnsTheSuccessfulResult()
    {
        // correctness-review follow-up on PR #442: disposing the scope AFTER a successful dispatch
        // must not overwrite that success with a failure if disposal itself throws -- otherwise a
        // committed write (e.g. a submitted ChangeProposal) gets reported to the model as a dispatch
        // failure, inviting a pointless retry of work that already landed.
        var innerProvider = new ServiceCollection()
            .AddScoped(_ => (IMediator)new SucceedingMediator())
            .BuildServiceProvider();
        var scopeFactory = new ThrowingDisposalScopeFactory(innerProvider);

        var result = await MediatorDispatchRunner.RunAsync(
            scopeFactory,
            async (mediator, ct) => { await mediator.Send(new Ping(), ct); return ToolResult.Ok("done"); },
            NullLogger.Instance,
            "test_tool",
            failureContext: "n/a",
            cancellationToken: CancellationToken.None);

        result.Success.Should().BeTrue(
            "a scope-disposal failure occurring after a successful dispatch must not overwrite the " +
            "already-obtained successful result");
    }

    [Fact]
    public async Task RunAsync_ScopeCreationThrows_ReturnsFailureInsteadOfThrowing()
    {
        // correctness-review follow-up on PR #442: this method's own doc comment claims a
        // "scope-creation ... failure" is logged and mapped, but scope creation originally sat
        // outside the try/catch entirely -- an already-disposed root provider during host shutdown
        // (or any other CreateAsyncScope() failure) escaped ExecuteAsync uncaught, reopening the
        // exact gap #428 exists to close.
        var scopeFactory = new ThrowingCreationScopeFactory();

        var result = await MediatorDispatchRunner.RunAsync(
            scopeFactory,
            async (mediator, ct) => { await mediator.Send(new Ping(), ct); return ToolResult.Ok("unreachable"); },
            NullLogger.Instance,
            "test_tool",
            failureContext: "n/a",
            cancellationToken: CancellationToken.None);

        result.Success.Should().BeFalse(
            "a scope-creation failure is a sandbox-level error — it must not throw out of RunAsync uncaught");
    }

    private sealed record Ping : IRequest<Unit>;

    /// <summary>An <see cref="IServiceScopeFactory"/> whose <see cref="CreateScope"/> always throws.</summary>
    private sealed class ThrowingCreationScopeFactory : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => throw new InvalidOperationException("Simulated scope creation failure.");
    }

    private sealed class SucceedingMediator : IMediator
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
            => Task.FromResult((TResponse)(object)Unit.Value);

        public Task<object?> Send(object request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default)
            where TRequest : IRequest
            => throw new NotSupportedException();

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification
            => Task.CompletedTask;
    }

    /// <summary>An <see cref="IServiceScopeFactory"/> whose every scope throws on disposal.</summary>
    private sealed class ThrowingDisposalScopeFactory : IServiceScopeFactory
    {
        private readonly IServiceProvider _serviceProvider;

        public ThrowingDisposalScopeFactory(IServiceProvider serviceProvider) => _serviceProvider = serviceProvider;

        public IServiceScope CreateScope() => new ThrowingDisposalScope(_serviceProvider);

        private sealed class ThrowingDisposalScope(IServiceProvider serviceProvider) : IServiceScope, IAsyncDisposable
        {
            public IServiceProvider ServiceProvider { get; } = serviceProvider;

            public void Dispose() => throw new InvalidOperationException("Simulated scope disposal failure.");

            public ValueTask DisposeAsync() =>
                ValueTask.FromException(new InvalidOperationException("Simulated scope disposal failure."));
        }
    }

    private sealed class ThrowingOceMediator : IMediator
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
            => throw new OperationCanceledException("Simulated internal timeout, unrelated to the caller's token.");

        public Task<object?> Send(object request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default)
            where TRequest : IRequest
            => throw new NotSupportedException();

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification
            => Task.CompletedTask;
    }
}
