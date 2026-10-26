using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Application.AI.Common.Interfaces.Agent;
using Application.Core.Compliance;
using Application.Core.CQRS.Compliance.ExportSubjectData;
using Domain.Common;
using Domain.Common.Compliance;
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
/// <c>POST /api/compliance/exports</c>, an operator's copy of a named person's data (#902): only a caller holding the
/// export role and signed in from an allowed tenant reaches the command; every refusal is recorded; the subject comes from
/// the body exactly as given, and the operator recorded is the authenticated caller, never a field the request could set.
/// </summary>
/// <remarks>
/// A copy hands one person's whole data to someone else, so the refusal paths matter as much as the success path: each
/// proves nothing was sent and the refusal was recorded through the real export audit, without the person's id.
/// </remarks>
public sealed class ComplianceExportControllerOperatorTests : IClassFixture<TestWebApplicationFactory>
{
    private const string Url = "/api/compliance/exports";
    private const string HomeTenant = "home-tenant";
    private readonly TestWebApplicationFactory _factory;
    private readonly ConcurrentQueue<AuditEntry> _records = new();

    /// <summary>Initialises the test class with the shared integration factory.</summary>
    public ComplianceExportControllerOperatorTests(TestWebApplicationFactory factory) => _factory = factory;

    private HttpClient Client(string? roles, string? tenant, params string[] allowedTenants)
    {
        // An operator of its own per test: the per-operator limit counts every request one caller makes.
        var client = _factory.CreateAuthedClient($"dpo-operator-{Guid.NewGuid():N}", roles, services =>
        {
            services.Replace(ServiceDescriptor.Singleton<IAuditSink>(new CapturingSink(_records)));
            services.PostConfigure<ErasureConfig>(c => c.OperatorTenantIds = [.. allowedTenants]);
        });
        if (tenant is not null)
            client.DefaultRequestHeaders.Add(TestAuthHandler.TenantHeader, tenant);
        return client;
    }

    private Func<ExportSubjectDataCommand?> CaptureSent(Result<PersonalDataExport>? result = null)
    {
        ExportSubjectDataCommand? sent = null;
        _factory.MockMediator
            .Setup(m => m.Send(It.IsAny<ExportSubjectDataCommand>(), It.IsAny<CancellationToken>()))
            .Callback<IRequest<Result<PersonalDataExport>>, CancellationToken>((c, _) => sent = (ExportSubjectDataCommand)c)
            .ReturnsAsync(result ?? Result<PersonalDataExport>.Success(new PersonalDataExport(
                "export-1",
                new DateTimeOffset(2026, 10, 26, 9, 0, 0, TimeSpan.Zero),
                [new ExportManifestEntry("conversations", ExportStatus.Included, 1)],
                [new ExportSection("conversations", [JsonSerializer.SerializeToElement(new { id = "c-1" })])])));
        return () => sent;
    }

    public static TheoryData<string?, string?, string[], string> Refusals => new()
    {
        { null, HomeTenant, [HomeTenant], "missing the export role" },
        // Erasing someone and reading their whole data are separate grants: the erasure role alone does not export.
        { ComplianceController.EraseSubjectRole, HomeTenant, [HomeTenant], "missing the export role" },
        { ComplianceExportController.ExportSubjectRole, HomeTenant, [], "operator export is not configured" },
        { ComplianceExportController.ExportSubjectRole, "customer-tenant", [HomeTenant], "operator's tenant may not export" },
        { ComplianceExportController.ExportSubjectRole, null, [HomeTenant], "operator's tenant may not export" },
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task ExportSubjectData_Refused_Returns403SendsNothingAndRecordsTheRefusal(
        string? roles, string? tenant, string[] allowedTenants, string reason)
    {
        var sent = CaptureSent();
        using var client = Client(roles, tenant, allowedTenants);

        var response = await client.PostAsJsonAsync(Url, new { subjectId = "victim-oid" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        sent().Should().BeNull("a refused operator must never reach the copy");
        var record = _records.Should().ContainSingle().Subject;
        record.Outcome.Should().Be(AuditOutcome.Denied);
        record.FailureReason.Should().Be(reason);
        record.ExecutorId.Should().StartWith("dpo-operator-");
        record.Metadata!.Values.Should().NotContain(v => v.Contains("victim-oid"), "the person is named only pseudonymously");
    }

    [Fact]
    public async Task ExportSubjectData_AllowedOperator_SendsTheSubjectAsGivenAndGetsTheCopyAsAFileNothingCaches()
    {
        var sent = CaptureSent();
        using var client = Client(ComplianceExportController.ExportSubjectRole, HomeTenant, HomeTenant);

        // An operatorId in the body is ignored: the operator is whoever authenticated.
        var response = await client.PostAsJsonAsync(Url, new { subjectId = "Subject-Mixed-Case", operatorId = "someone-else" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        sent()!.SubjectId.Should().Be("Subject-Mixed-Case");
        sent()!.OperatorId.Should().StartWith("dpo-operator-");
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        response.Content.Headers.ContentDisposition!.DispositionType.Should().Be("attachment");
        _records.Should().BeEmpty("the command records the copy itself; the controller records only refusals");
    }

    [Fact]
    public async Task ExportSubjectData_ValidationFailure_Returns400AndRecordsTheRefusal()
    {
        CaptureSent(Result<PersonalDataExport>.ValidationFailure(["SubjectId must be given exactly as issued."]));
        using var client = Client(ComplianceExportController.ExportSubjectRole, HomeTenant, HomeTenant);

        var response = await client.PostAsJsonAsync(Url, new { subjectId = " padded " });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _records.Should().ContainSingle().Which.FailureReason.Should().Be("invalid subject id");
    }

    [Fact]
    public async Task ExportSubjectData_ConfigurationInvalidAfterStartup_Returns503AndRecordsTheRefusal()
    {
        var sent = CaptureSent();
        var invalid = new Mock<IOptionsMonitor<ErasureConfig>>();
        invalid.SetupGet(m => m.CurrentValue)
            .Throws(new OptionsValidationException(Options.DefaultName, typeof(ErasureConfig), ["bad key"]));
        using var client = _factory.CreateAuthedClient("dpo-operator-cfg", ComplianceExportController.ExportSubjectRole, services =>
        {
            services.Replace(ServiceDescriptor.Singleton<IAuditSink>(new CapturingSink(_records)));
            services.Replace(ServiceDescriptor.Singleton(invalid.Object));
        });
        client.DefaultRequestHeaders.Add(TestAuthHandler.TenantHeader, HomeTenant);

        var response = await client.PostAsJsonAsync(Url, new { subjectId = "victim-oid" });

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        sent().Should().BeNull();
        _records.Should().ContainSingle().Which.FailureReason.Should().Be("export configuration invalid");
    }

    [Fact]
    public async Task ExportSubjectData_ExceedingPerOperatorWindow_Returns429AndStopsWritingRecords()
    {
        // Every refused request writes an audit record, so without a limit any signed-in caller could flood the trail.
        CaptureSent();
        using var client = _factory.CreateAuthedClient("flooding-exporter", roles: null, services =>
            services.Replace(ServiceDescriptor.Singleton<IAuditSink>(new CapturingSink(_records))));

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

    private sealed class CapturingSink(ConcurrentQueue<AuditEntry> records) : IAuditSink
    {
        public ValueTask RecordAsync(AuditEntry entry, CancellationToken cancellationToken = default)
        {
            if (entry.Action == AuditedExport.AuditAction)
                records.Enqueue(entry);
            return ValueTask.CompletedTask;
        }
    }
}
