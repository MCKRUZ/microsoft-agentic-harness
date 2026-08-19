using Application.Core.CQRS.RAG.IngestDocument;
using FluentAssertions;
using Infrastructure.AI.Tests.Resilience;
using Infrastructure.AI.Tests.Support;
using Infrastructure.AI.Tools;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Infrastructure.AI.Tests.Tools;

/// <summary>
/// Unit tests for <see cref="DocumentIngestTool"/>'s exception handling around its MediatR dispatch
/// (#428, mirroring #421's <c>IacSandboxRunner</c> fix and #426's <c>WorkspaceCommandRunner</c> fix):
/// scope creation and <see cref="IMediator"/> resolution/dispatch must never throw uncaught out of
/// <see cref="DocumentIngestTool.ExecuteAsync"/>.
/// </summary>
public sealed class DocumentIngestToolTests
{
    [Fact]
    public async Task Ingest_ScopeFactoryHasNoMediatorRegistered_ReturnsFailureInsteadOfThrowing()
    {
        var servicesWithNoMediator = new ServiceCollection().BuildServiceProvider();
        var sut = new DocumentIngestTool(
            servicesWithNoMediator.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<DocumentIngestTool>.Instance);

        var result = await sut.ExecuteAsync(
            "ingest",
            new Dictionary<string, object?> { ["uri"] = "file:///tmp/doc.md" });

        result.Success.Should().BeFalse(
            "a DI-resolution failure for IMediator is a sandbox-level error — it must not throw out of ExecuteAsync uncaught");
    }

    [Fact]
    public async Task Ingest_MediatorThrows_ReturnsFailureInsteadOfThrowing()
    {
        var mediator = new ThrowingMediator();
        var sut = new DocumentIngestTool(TestScopeFactory.For(mediator), NullLogger<DocumentIngestTool>.Instance);

        var result = await sut.ExecuteAsync(
            "ingest",
            new Dictionary<string, object?> { ["uri"] = "file:///tmp/doc.md" });

        result.Success.Should().BeFalse(
            "an exception from the MediatR pipeline must be caught and mapped to a failed ToolResult, not thrown out of ExecuteAsync");
    }

    [Theory]
    [InlineData(
        "https://acct.blob.core.windows.net/c/d.md?sv=2024&sig=SUPERSECRETSIG",
        "SUPERSECRETSIG")]
    [InlineData(
        "https://svc:P4ssw0rd@internal.example/doc.md",
        "P4ssw0rd")]
    public async Task Ingest_DispatchFails_NeverLogsCredentialBearingUriComponents(
        string credentialBearingUri, string credential)
    {
        // security-review MEDIUM on PR #442: GetLeftPart(UriPartial.Path) drops the query string
        // (closing the SAS-token case) but keeps userinfo verbatim (https://user:pass@host/...) --
        // both must be scrubbed before this failure path's URI reaches an error log.
        var mediator = new ThrowingMediator();
        var logger = new RecordingLogger<DocumentIngestTool>();
        var sut = new DocumentIngestTool(TestScopeFactory.For(mediator), logger);

        var result = await sut.ExecuteAsync(
            "ingest",
            new Dictionary<string, object?> { ["uri"] = credentialBearingUri });

        result.Success.Should().BeFalse();
        logger.Entries.Should().NotBeEmpty();
        logger.Entries.Should().OnlyContain(e => !e.Message.Contains(credential),
            $"the credential '{credential}' from '{credentialBearingUri}' must never reach a log entry");
    }

    [Fact]
    public async Task Ingest_MediatorReturnsSuccess_SurfacesJobDetails()
    {
        var mediator = new RecordingMediator(new IngestDocumentResult
        {
            JobId = "job-1",
            ChunksProduced = 3,
            TokensEmbedded = 120,
            Duration = TimeSpan.FromSeconds(2),
            Success = true
        });
        var sut = new DocumentIngestTool(TestScopeFactory.For(mediator), NullLogger<DocumentIngestTool>.Instance);

        var result = await sut.ExecuteAsync(
            "ingest",
            new Dictionary<string, object?> { ["uri"] = "file:///tmp/doc.md" });

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("job-1");
        mediator.DispatchedCommands.Should().ContainSingle()
            .Which.DocumentUri.Should().Be(new Uri("file:///tmp/doc.md"));
    }

    /// <summary>
    /// Minimal <see cref="IMediator"/> that records dispatched <see cref="IngestDocumentCommand"/>
    /// instances and returns a preconfigured <see cref="IngestDocumentResult"/>.
    /// </summary>
    private sealed class RecordingMediator : IMediator
    {
        private readonly IngestDocumentResult _response;

        public RecordingMediator(IngestDocumentResult response) => _response = response;

        public List<IngestDocumentCommand> DispatchedCommands { get; } = new();

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            if (request is IngestDocumentCommand command)
            {
                DispatchedCommands.Add(command);
                return Task.FromResult((TResponse)(object)_response);
            }

            throw new NotSupportedException($"RecordingMediator does not handle {request.GetType().Name}.");
        }

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
