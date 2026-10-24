using System.Collections.Concurrent;
using System.Net;
using Application.AI.Common.Interfaces.Agent;
using Application.Core.Compliance;
using Application.Core.CQRS.Compliance.EraseMyData;
using Domain.AI.KnowledgeGraph.Models;
using Domain.Common;
using Domain.Common.Models;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;
using Xunit;

namespace Presentation.AgentHub.Tests.Controllers;

/// <summary>
/// <c>POST /api/compliance/erase-my-data</c> refuses an application's own token (#907). Every caller of that application
/// shares one identity, so "erase my data" would erase all of their data. A person's token reaches the command.
/// </summary>
/// <remarks>
/// <see cref="TestWebApplicationFactory"/> replaces the mediator host-wide, so these tests stub
/// <see cref="TestWebApplicationFactory.MockMediator"/> and prove what was, or was not, sent.
/// </remarks>
public sealed class ComplianceControllerEraseMyDataTests : IClassFixture<TestWebApplicationFactory>
{
    private const string Url = "/api/compliance/erase-my-data";
    private readonly TestWebApplicationFactory _factory;
    private readonly ConcurrentQueue<AuditEntry> _records = new();

    /// <summary>Initialises the test class with the shared integration factory.</summary>
    public ComplianceControllerEraseMyDataTests(TestWebApplicationFactory factory) => _factory = factory;

    private HttpClient Client(string? claimShape)
    {
        var client = _factory.CreateAuthedClient("caller-oid", roles: null, services =>
            services.Replace(ServiceDescriptor.Singleton<IAuditSink>(new CapturingSink(_records))));
        if (claimShape is not null)
            client.DefaultRequestHeaders.Add(TestAuthHandler.ClaimShapeHeader, claimShape);
        return client;
    }

    private Func<bool> CaptureSent()
    {
        var sent = false;
        _factory.MockMediator
            .Setup(m => m.Send(It.IsAny<EraseMyDataCommand>(), It.IsAny<CancellationToken>()))
            .Callback<IRequest<Result<ErasureReceipt>>, CancellationToken>((_, _) => sent = true)
            .ReturnsAsync(Result<ErasureReceipt>.Success(new ErasureReceipt
            {
                RequestId = "receipt-1",
                ScopeId = "caller-oid",
                RequestedAt = DateTimeOffset.UnixEpoch,
                CompletedAt = DateTimeOffset.UnixEpoch,
                NodesDeleted = 0,
                EdgesDeleted = 0,
                FeedbackWeightsDeleted = 0,
                VectorEmbeddingsDeleted = 0,
            }));
        return () => sent;
    }

    [Theory]
    [InlineData("app-only")]
    [InlineData("app-idtyp")]
    public async Task EraseMyData_ApplicationToken_Returns403SendsNothingAndRecordsTheRefusal(string claimShape)
    {
        var sent = CaptureSent();
        using var client = Client(claimShape);

        var response = await client.PostAsync(Url, content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).Should().Contain("needs a person's sign-in");
        sent().Should().BeFalse("an application token must never reach the erasure");
        var record = _records.Should().ContainSingle().Subject;
        record.Outcome.Should().Be(AuditOutcome.Denied);
        record.FailureReason.Should().Be("application token");
    }

    [Theory]
    [InlineData("person")]
    [InlineData("oid-only")]
    [InlineData("sub-only")]
    [InlineData(null)]
    public async Task EraseMyData_APersonsToken_ReachesTheCommand(string? claimShape)
    {
        // Including tokens with no oid, or no sub: a provider that issues only one of them is never read as an application.
        var sent = CaptureSent();
        using var client = Client(claimShape);

        var response = await client.PostAsync(Url, content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        sent().Should().BeTrue();
    }

    private sealed class CapturingSink(ConcurrentQueue<AuditEntry> records) : IAuditSink
    {
        public ValueTask RecordAsync(AuditEntry entry, CancellationToken cancellationToken = default)
        {
            if (entry.Action == AuditedErasure.AuditAction)
                records.Enqueue(entry);
            return ValueTask.CompletedTask;
        }
    }
}
