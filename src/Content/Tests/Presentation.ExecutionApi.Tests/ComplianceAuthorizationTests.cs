using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Presentation.Common.Compliance;
using Xunit;
using static Presentation.ExecutionApi.Tests.ComplianceHost;

namespace Presentation.ExecutionApi.Tests;

/// <summary>
/// Who may erase and copy what in the Execution API (#1003): the same rules AgentHub applies, because both hosts serve
/// the one shared compliance API. No sign-in, no request; a role or tenant short of an operator's, nothing erased or
/// copied; the host's anonymous mode, which makes every caller one person, refused outright; and the rate limits apply.
/// </summary>
public sealed class ComplianceAuthorizationTests
{
    public static TheoryData<string, object?> Endpoints => new()
    {
        { "/api/compliance/erase-my-data", null },
        { "/api/compliance/export-my-data", null },
        { "/api/compliance/erasures", new { subjectId = "someone" } },
        { "/api/compliance/exports", new { subjectId = "someone" } },
        { "/api/compliance/document-removals", new { documentId = "0123456789ABCDEF" } },
    };

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task AnUnauthenticatedCaller_IsChallenged(string path, object? body)
    {
        using var factory = Create();
        using var client = factory.CreateClient();

        var response = await client.PostAsync(path, body is null ? null : JsonContent.Create(body));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    public static TheoryData<string, string?, string?> OperatorRefusals => new()
    {
        // No role at all, the wrong compliance role, and the right role from a tenant not trusted to erase.
        { "/api/compliance/erasures", null, OperatorTenant },
        { "/api/compliance/erasures", ComplianceExportController.ExportSubjectRole, OperatorTenant },
        { "/api/compliance/erasures", ComplianceController.EraseSubjectRole, "customer-tenant" },
        { "/api/compliance/exports", null, OperatorTenant },
        { "/api/compliance/exports", ComplianceController.EraseSubjectRole, OperatorTenant },
        { "/api/compliance/exports", ComplianceExportController.ExportSubjectRole, "customer-tenant" },
    };

    [Theory]
    [MemberData(nameof(OperatorRefusals))]
    public async Task AnOperatorRequest_WithoutTheRoleAndAnAllowedTenant_IsRefusedAndTouchesNothing(
        string path, string? roles, string tenant)
    {
        using var factory = Create();
        using var client = factory.CreateClient();
        var subject = await SeedAsync(factory.Services, NewPerson("subject"));

        var response = await client.SendAsync(Post(path, NewPerson("caller"), new { subjectId = subject.OwnerId }, tenant, roles));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).Should().NotContain(subject.Message);
        (await StillHeldAsync(factory.Services, subject)).Should().Be((true, true));
    }

    [Fact]
    public async Task DocumentRemoval_WithoutItsRole_IsRefused()
    {
        using var factory = Create();
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Post(
            "/api/compliance/document-removals", NewPerson("caller"), new { documentId = "0123456789ABCDEF" },
            OperatorTenant, ComplianceController.EraseSubjectRole));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData("/api/compliance/erase-my-data")]
    [InlineData("/api/compliance/export-my-data")]
    public async Task AnonymousMode_RefusesSelfService_BecauseEveryCallerIsOnePerson(string path)
    {
        // The host's shipped Development configuration is anonymous: every caller is "anonymous-dev", so "my data" is
        // everyone's runs and conversations, and a copy of it would disclose them all.
        using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        var response = await client.PostAsync(path, content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).Should().Contain("sign-in is bypassed");
    }

    [Theory]
    [InlineData("/api/compliance/erasures", "subjectId", "someone")]
    [InlineData("/api/compliance/exports", "subjectId", "someone")]
    [InlineData("/api/compliance/document-removals", "documentId", "0123456789ABCDEF")]
    public async Task AnonymousMode_RefusesOperatorActions(string path, string field, string value)
    {
        using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(path, new Dictionary<string, string> { [field] = value });

        // Refused as a bypass, not merely for lacking a role: the gate must not depend on the anonymous principal never
        // being granted one.
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).Should().Contain("not available while sign-in is bypassed");
    }

    [Fact]
    public async Task SelfServiceErasure_PastTheHourlyAllowance_IsRefusedWith429()
    {
        using var factory = Create();
        using var client = factory.CreateClient();
        var caller = NewPerson("repeat");

        for (var i = 0; i < ComplianceRateLimits.SelfServicePerHour; i++)
            (await client.SendAsync(Post("/api/compliance/erase-my-data", caller))).StatusCode.Should().Be(HttpStatusCode.OK);

        var refused = await client.SendAsync(Post("/api/compliance/erase-my-data", caller));
        var someoneElse = await client.SendAsync(Post("/api/compliance/erase-my-data", NewPerson("other")));

        refused.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        someoneElse.StatusCode.Should().Be(HttpStatusCode.OK, "the allowance is per person, not shared by the host");
    }
}
