using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Application.AI.Common.Interfaces.Agent;
using Application.Core.Compliance;
using Application.Core.CQRS.Compliance.EraseSubjectData;
using Application.Core.CQRS.Compliance.RemoveDocument;
using Domain.AI.KnowledgeGraph.Models;
using Domain.Common;
using Domain.Common.Config.AI;
using Domain.Common.Models;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Moq;
using Presentation.Common.Compliance;
using Presentation.AgentHub.Controllers;
using Xunit;

namespace Presentation.AgentHub.Tests.Controllers;

/// <summary>
/// <c>POST /api/compliance/document-removals</c>, operator removal of a source document (#901): only a caller holding the
/// document-removal role and signed in from an allowed tenant reaches the command; every refusal is recorded with the
/// document asked for; and the operator recorded is the authenticated caller, never a field the request could set.
/// </summary>
/// <remarks>
/// <see cref="TestWebApplicationFactory"/> replaces the mediator host-wide, so the tests that reach dispatch stub
/// <see cref="TestWebApplicationFactory.MockMediator"/>; the refusal tests prove nothing was sent and the refusal was
/// recorded through the real document-removal audit, into a capturing sink.
/// </remarks>
public sealed class ComplianceControllerRemoveDocumentTests : IClassFixture<TestWebApplicationFactory>
{
    private const string Url = "/api/compliance/document-removals";
    private const string HomeTenant = "home-tenant";
    private const string DocumentId = "0123456789ABCDEF";
    private readonly TestWebApplicationFactory _factory;
    private readonly ConcurrentQueue<AuditEntry> _records = new();

    /// <summary>Initialises the test class with the shared integration factory.</summary>
    public ComplianceControllerRemoveDocumentTests(TestWebApplicationFactory factory) => _factory = factory;

    private static ErasureReceipt Receipt() => new()
    {
        RequestId = "receipt-1",
        ScopeId = "document",
        RequestedAt = DateTimeOffset.UnixEpoch,
        CompletedAt = DateTimeOffset.UnixEpoch,
        NodesDeleted = 0,
        EdgesDeleted = 0,
        FeedbackWeightsDeleted = 0,
        VectorEmbeddingsDeleted = 0,
    };

    private HttpClient Client(string? roles, string? tenant, params string[] allowedTenants) =>
        ClientAs("dpo-operator-7", roles, tenant, allowedTenants);

    private HttpClient ClientAs(string user, string? roles, string? tenant, params string[] allowedTenants)
    {
        var client = _factory.CreateAuthedClient(user, roles, services =>
        {
            services.Replace(ServiceDescriptor.Singleton<IAuditSink>(new CapturingSink(_records)));
            services.PostConfigure<ErasureConfig>(c => c.OperatorTenantIds = [.. allowedTenants]);
        });
        if (tenant is not null)
            client.DefaultRequestHeaders.Add(TestAuthHandler.TenantHeader, tenant);
        return client;
    }

    private Func<RemoveDocumentCommand?> CaptureSent(Result<ErasureReceipt>? result = null)
    {
        RemoveDocumentCommand? sent = null;
        _factory.MockMediator
            .Setup(m => m.Send(It.IsAny<RemoveDocumentCommand>(), It.IsAny<CancellationToken>()))
            .Callback<IRequest<Result<ErasureReceipt>>, CancellationToken>((c, _) => sent = (RemoveDocumentCommand)c)
            .ReturnsAsync(result ?? Result<ErasureReceipt>.Success(Receipt()));
        return () => sent;
    }

    public static TheoryData<string?, string?, string[], string> Refusals => new()
    {
        { null, HomeTenant, [HomeTenant], "missing the document-removal role" },
        { ComplianceController.EraseSubjectRole, HomeTenant, [HomeTenant], "missing the document-removal role" },
        { ComplianceController.RemoveDocumentRole, HomeTenant, [], "operator document removal is not configured" },
        { ComplianceController.RemoveDocumentRole, "customer-tenant", [HomeTenant], "operator's tenant may not remove documents" },
        { ComplianceController.RemoveDocumentRole, null, [HomeTenant], "operator's tenant may not remove documents" },
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task RemoveDocument_Refused_Returns403SendsNothingAndRecordsTheRefusal(
        string? roles, string? tenant, string[] allowedTenants, string reason)
    {
        // The erasure role is a different grant: erasing a person does not entitle anyone to delete the corpus.
        var sent = CaptureSent();
        using var client = Client(roles, tenant, allowedTenants);

        var response = await client.PostAsJsonAsync(Url, new { documentId = DocumentId });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        sent().Should().BeNull("a refused operator must never reach the removal");
        var record = _records.Should().ContainSingle().Subject;
        record.Outcome.Should().Be(AuditOutcome.Denied);
        record.FailureReason.Should().Be(reason);
        record.ExecutorId.Should().Be("dpo-operator-7");
        record.Metadata!["document_id"].Should().Be(DocumentId);
    }

    [Fact]
    public async Task EraseSubjectData_DocumentRemovalRoleOnly_IsRefused()
    {
        // And the other way round: removing documents does not entitle anyone to erase a person.
        _factory.MockMediator
            .Setup(m => m.Send(It.IsAny<EraseSubjectDataCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<ErasureReceipt>.Success(Receipt()));
        using var client = Client(ComplianceController.RemoveDocumentRole, HomeTenant, HomeTenant);

        var response = await client.PostAsJsonAsync("/api/compliance/erasures", new { subjectId = "victim-oid" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _factory.MockMediator.Verify(
            m => m.Send(It.IsAny<EraseSubjectDataCommand>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RemoveDocument_AllowedOperator_SendsTheDocumentAndTheCallerAsOperator()
    {
        var sent = CaptureSent();
        using var client = Client(ComplianceController.RemoveDocumentRole, HomeTenant, HomeTenant);

        // An operatorId in the body is ignored: the operator is whoever authenticated.
        var response = await client.PostAsJsonAsync(Url, new { documentId = DocumentId, operatorId = "someone-else" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        sent().Should().NotBeNull();
        sent()!.DocumentId.Should().Be(DocumentId);
        sent()!.OperatorId.Should().Be("dpo-operator-7");
        _records.Should().BeEmpty("the command records the removal itself; the controller records only refusals");
    }

    [Fact]
    public async Task RemoveDocument_ValidationFailure_Returns400AndRecordsTheRefusalWithoutTheInput()
    {
        CaptureSent(Result<ErasureReceipt>.ValidationFailure(["DocumentId must be 16 upper-case hexadecimal digits."]));
        using var client = Client(ComplianceController.RemoveDocumentRole, HomeTenant, HomeTenant);

        var response = await client.PostAsJsonAsync(Url, new { documentId = "not-a-document-id" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var record = _records.Should().ContainSingle().Subject;
        record.FailureReason.Should().Be("invalid document id");
        record.Metadata!["document_id"].Should().Be(AuditedDocumentRemoval.DocumentInvalid,
            "unvalidated input of any length is never written into the trail");
    }

    [Fact]
    public async Task RemoveDocument_ConfigurationInvalidAfterStartup_Returns503AndRecordsTheRefusal()
    {
        var sent = CaptureSent();
        var invalid = new Mock<IOptionsMonitor<ErasureConfig>>();
        invalid.SetupGet(m => m.CurrentValue)
            .Throws(new OptionsValidationException(Options.DefaultName, typeof(ErasureConfig), ["bad key"]));
        using var client = _factory.CreateAuthedClient("dpo-operator-7", ComplianceController.RemoveDocumentRole, services =>
        {
            services.Replace(ServiceDescriptor.Singleton<IAuditSink>(new CapturingSink(_records)));
            services.Replace(ServiceDescriptor.Singleton(invalid.Object));
        });
        client.DefaultRequestHeaders.Add(TestAuthHandler.TenantHeader, HomeTenant);

        var response = await client.PostAsJsonAsync(Url, new { documentId = DocumentId });

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        sent().Should().BeNull();
        _records.Should().ContainSingle().Which.FailureReason.Should().Be("document removal configuration invalid");
    }

    [Fact]
    public async Task RemoveDocument_ExceedingPerCallerWindow_Returns429AndStopsWritingRecords()
    {
        // Every refused request writes an audit record, so without a limit any signed-in caller could flood the trail.
        CaptureSent();
        using var client = ClientAs("flooding-remover", roles: null, tenant: null);
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 31; i++)
        {
            using var response = await client.PostAsJsonAsync(Url, new { documentId = DocumentId });
            statuses.Add(response.StatusCode);
        }

        statuses.Take(30).Should().OnlyContain(s => s == HttpStatusCode.Forbidden);
        statuses[30].Should().Be(HttpStatusCode.TooManyRequests);
        _records.Should().HaveCount(30, "a rate-limited request never reaches the point that writes a record");
    }

    [Fact]
    public async Task RemoveDocument_LowerCaseMethod_IsStillRateLimited()
    {
        // Routing matches the method name in any case, so the limit must too, or "post" walks past it.
        CaptureSent();
        using var client = ClientAs("lower-case-remover", roles: null, tenant: null);
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 31; i++)
        {
            using var request = new HttpRequestMessage(new HttpMethod("post"), Url)
            {
                Content = JsonContent.Create(new { documentId = DocumentId }),
            };
            using var response = await client.SendAsync(request);
            statuses.Add(response.StatusCode);
        }

        statuses.Take(30).Should().OnlyContain(s => s == HttpStatusCode.Forbidden, "the lower-case method reaches the action");
        statuses[30].Should().Be(HttpStatusCode.TooManyRequests);
    }

    private sealed class CapturingSink(ConcurrentQueue<AuditEntry> records) : IAuditSink
    {
        public ValueTask RecordAsync(AuditEntry entry, CancellationToken cancellationToken = default)
        {
            if (entry.Action == AuditedDocumentRemoval.AuditAction)
                records.Enqueue(entry);
            return ValueTask.CompletedTask;
        }
    }
}
