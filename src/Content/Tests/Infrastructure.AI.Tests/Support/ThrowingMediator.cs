using MediatR;

namespace Infrastructure.AI.Tests.Support;

/// <summary>
/// Minimal <see cref="IMediator"/> whose <c>Send</c> always throws — models a downstream MediatR
/// pipeline failure (e.g. a handler-level dependency outage) for tools that must map a dispatch
/// exception to a failed <c>ToolResult</c> rather than let it escape <c>ITool.ExecuteAsync</c>
/// uncaught (#428). Previously duplicated byte-for-byte in <c>WorkspaceWriteFileToolTests</c> and
/// <c>DocumentIngestToolTests</c>.
/// </summary>
internal sealed class ThrowingMediator : IMediator
{
    public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("Simulated MediatR pipeline failure.");

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
