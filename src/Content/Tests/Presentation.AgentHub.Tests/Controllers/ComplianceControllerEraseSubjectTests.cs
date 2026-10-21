using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Application.AI.Common.Interfaces.Agent;
using Application.Core.Compliance;
using Application.Core.CQRS.Compliance.EraseSubjectData;
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
using Presentation.AgentHub.Controllers;
using Xunit;

namespace Presentation.AgentHub.Tests.Controllers;

/// <summary>
/// <c>POST /api/compliance/erasures</c>, operator-initiated erasure (#900): only a caller holding the erasure role and
/// signed in from an allowed tenant reaches the command; every refusal is recorded; the subject comes from the body
/// exactly as given, and the operator recorded is the authenticated caller, never a field the request could set.
/// </summary>
/// <remarks>
/// <see cref="TestWebApplicationFactory"/> replaces the mediator host-wide, so the tests that reach dispatch stub
/// <see cref="TestWebApplicationFactory.MockMediator"/>; the refusal tests prove nothing was sent and the refusal was
/// recorded through the real erasure audit, into a capturing sink.
/// </remarks>
public sealed class ComplianceControllerEraseSubjectTests : IClassFixture<TestWebApplicationFactory>
{
    private const string Url = "/api/compliance/erasures";
    private const string HomeTenant = "home-tenant";
    private readonly TestWebApplicationFactory _factory;
    private readonly ConcurrentQueue<AuditEntry> _records = new();

    /// <summary>Initialises the test class with the shared integration factory.</summary>
    public ComplianceControllerEraseSubjectTests(TestWebApplicationFactory factory) => _factory = factory;

    private static ErasureReceipt Receipt() => new()
    {
        RequestId = "receipt-1",
        ScopeId = "subject",
        RequestedAt = DateTimeOffset.UnixEpoch,
        CompletedAt = DateTimeOffset.UnixEpoch,
        NodesDeleted = 0,
        EdgesDeleted = 0,
        FeedbackWeightsDeleted = 0,
        VectorEmbeddingsDeleted = 0,
    };

    private HttpClient Client(string? roles, string? tenant, params string[] allowedTenants)
    {
        var client = _factory.CreateAuthedClient("dpo-operator-7", roles, services =>
        {
            services.Replace(ServiceDescriptor.Singleton<IAuditSink>(new CapturingSink(_records)));
            services.PostConfigure<ErasureConfig>(c => c.OperatorTenantIds = [.. allowedTenants]);
        });
        if (tenant is not null)
            client.DefaultRequestHeaders.Add(TestAuthHandler.TenantHeader, tenant);
        return client;
    }

    private Func<EraseSubjectDataCommand?> CaptureSent(Result<ErasureReceipt>? result = null)
    {
        EraseSubjectDataCommand? sent = null;
        _factory.MockMediator
            .Setup(m => m.Send(It.IsAny<EraseSubjectDataCommand>(), It.IsAny<CancellationToken>()))
            .Callback<IRequest<Result<ErasureReceipt>>, CancellationToken>((c, _) => sent = (EraseSubjectDataCommand)c)
            .ReturnsAsync(result ?? Result<ErasureReceipt>.Success(Receipt()));
        return () => sent;
    }

    public static TheoryData<string?, string?, string[], string> Refusals => new()
    {
        { null, HomeTenant, [HomeTenant], "missing the erasure role" },
        { "AgentHub.Traces.ReadAll,Harness.Approvals.Admin", HomeTenant, [HomeTenant], "missing the erasure role" },
        { ComplianceController.EraseSubjectRole, HomeTenant, [], "operator erasure is not configured" },
        { ComplianceController.EraseSubjectRole, "customer-tenant", [HomeTenant], "operator's tenant may not erase" },
        { ComplianceController.EraseSubjectRole, null, [HomeTenant], "operator's tenant may not erase" },
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task EraseSubjectData_Refused_Returns403SendsNothingAndRecordsTheRefusal(
        string? roles, string? tenant, string[] allowedTenants, string reason)
    {
        // A role is not enough: each organisation's administrator assigns roles in their own tenant, and erasure reaches
        // every tenant, so a customer granting themselves the role must still be refused.
        var sent = CaptureSent();
        using var client = Client(roles, tenant, allowedTenants);

        var response = await client.PostAsJsonAsync(Url, new { subjectId = "victim-oid" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        sent().Should().BeNull("a refused operator must never reach the erasure");
        var record = _records.Should().ContainSingle().Subject;
        record.Outcome.Should().Be(AuditOutcome.Denied);
        record.FailureReason.Should().Be(reason);
        record.ExecutorId.Should().Be("dpo-operator-7");
        record.Metadata!.Values.Should().NotContain(v => v.Contains("victim-oid"));
    }

    [Fact]
    public async Task EraseSubjectData_AllowedOperator_SendsTheSubjectAsGivenAndTheCallerAsOperator()
    {
        var sent = CaptureSent();
        using var client = Client(ComplianceController.EraseSubjectRole, HomeTenant, HomeTenant);

        // An operatorId in the body is ignored: the operator is whoever authenticated.
        var response = await client.PostAsJsonAsync(Url, new { subjectId = "Subject-Mixed-Case", operatorId = "someone-else" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        sent().Should().NotBeNull();
        sent()!.SubjectId.Should().Be("Subject-Mixed-Case");
        sent()!.OperatorId.Should().Be("dpo-operator-7");
        _records.Should().BeEmpty("the command records the erasure itself; the controller records only refusals");
    }

    [Fact]
    public async Task EraseSubjectData_ValidationFailure_Returns400AndRecordsTheRefusal()
    {
        CaptureSent(Result<ErasureReceipt>.ValidationFailure(["SubjectId must be given exactly as issued."]));
        using var client = Client(ComplianceController.EraseSubjectRole, HomeTenant, HomeTenant);

        var response = await client.PostAsJsonAsync(Url, new { subjectId = " padded " });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _records.Should().ContainSingle().Which.FailureReason.Should().Be("invalid subject id");
    }

    [Fact]
    public async Task EraseSubjectData_ConfigurationInvalidAfterStartup_Returns503AndRecordsTheRefusal()
    {
        var sent = CaptureSent();
        var invalid = new Mock<IOptionsMonitor<ErasureConfig>>();
        invalid.SetupGet(m => m.CurrentValue)
            .Throws(new OptionsValidationException(Options.DefaultName, typeof(ErasureConfig), ["bad key"]));
        using var client = _factory.CreateAuthedClient("dpo-operator-7", ComplianceController.EraseSubjectRole, services =>
        {
            services.Replace(ServiceDescriptor.Singleton<IAuditSink>(new CapturingSink(_records)));
            services.Replace(ServiceDescriptor.Singleton(invalid.Object));
        });
        client.DefaultRequestHeaders.Add(TestAuthHandler.TenantHeader, HomeTenant);

        var response = await client.PostAsJsonAsync(Url, new { subjectId = "victim-oid" });

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        sent().Should().BeNull();
        _records.Should().ContainSingle().Which.FailureReason.Should().Be("erasure configuration invalid");
    }

    [Fact]
    public async Task EraseSubjectData_ExceedingPerCallerWindow_Returns429AndStopsWritingRecords()
    {
        // Every refused request writes an audit record, so without a limit any signed-in caller could flood the trail.
        CaptureSent();
        var client = _factory.CreateAuthedClient("flooding-caller", roles: null, services =>
            services.Replace(ServiceDescriptor.Singleton<IAuditSink>(new CapturingSink(_records))));
        using (client)
        {
            var statuses = new List<HttpStatusCode>();
            for (var i = 0; i < 31; i++)
            {
                using var response = await client.PostAsJsonAsync(Url, new { subjectId = "victim-oid" });
                statuses.Add(response.StatusCode);
            }

            statuses.Take(30).Should().OnlyContain(s => s == HttpStatusCode.Forbidden);
            statuses[30].Should().Be(HttpStatusCode.TooManyRequests);
            _records.Should().HaveCount(30, "a rate-limited request never reaches the point that writes a record");
        }
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
