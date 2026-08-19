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

    private sealed record Ping : IRequest<Unit>;

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
