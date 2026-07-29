using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Presentation.ExecutionApi.Tests.WorkflowRequests;

namespace Presentation.ExecutionApi.Tests;

/// <summary>
/// Proves that a submitted workflow is confined to the caller who submitted it, driven through the
/// real HTTP pipeline with two distinct authenticated identities.
/// </summary>
/// <remarks>
/// <para>
/// Store-level tests already cover the scope filter, but the handler's most security-load-bearing
/// claim — that one caller cannot reach another's workflow by naming its identifier — was only ever
/// verified a layer below where it is asserted. A store test passes even if the host forgets to mount
/// the scope middleware, which is a defect this repository has actually shipped.
/// </para>
/// <para>
/// The identifier used here is real and current: the second caller asks for a workflow that provably
/// exists, one it simply does not own. A test using a random identifier would pass against a
/// completely unscoped store.
/// </para>
/// </remarks>
public sealed class WorkflowOwnershipIsolationTests
{
    private static WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services
                .AddAuthentication(HeaderIdentityAuthenticationHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, HeaderIdentityAuthenticationHandler>(
                    HeaderIdentityAuthenticationHandler.SchemeName, _ => { })));

    private static HttpRequestMessage Submit(object body, string oid, string tenant)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/workflows")
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Add(HeaderIdentityAuthenticationHandler.UserHeader, oid);
        request.Headers.Add(HeaderIdentityAuthenticationHandler.TenantHeader, tenant);
        return request;
    }

    /// <summary>
    /// Reads the minted workflow id, asserting the submission actually succeeded first. Without the
    /// status assertion a failed submission surfaces as a KeyNotFoundException from the JSON read,
    /// which says nothing about why.
    /// </summary>
    private static async Task<Guid> WorkflowIdOfAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Created, "submission failed with body: {0}", body);
        return JsonDocument.Parse(body).RootElement.GetProperty("workflowId").GetGuid();
    }

    [Fact]
    public async Task AWorkflowSubmittedByOneCaller_CannotBeReferencedAsAChildByAnother()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var owned = await client.SendAsync(Submit(Definition([LlmStep("step")]), "alice", "acme"));
        var childId = await WorkflowIdOfAsync(owned);

        var stolen = await client.SendAsync(Submit(
            Definition([SubPlanStep("invoke-someone-elses", childId)]),
            "mallory", "acme"));

        // Rejected, and rejected as "does not exist or is not available" — the same answer a genuinely
        // unknown identifier gets, so the response is not an existence oracle for other callers' work.
        stolen.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await stolen.Content.ReadAsStringAsync()).Should().NotContain("alice");
    }

    [Fact]
    public async Task TheSubmittingCaller_CanReferenceTheirOwnWorkflow()
    {
        // The other half of the claim: isolation that also blocked the owner would pass the test above
        // while making the feature useless.
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var owned = await client.SendAsync(Submit(Definition([LlmStep("step")]), "alice", "acme"));
        var childId = await WorkflowIdOfAsync(owned);

        var reused = await client.SendAsync(Submit(
            Definition([SubPlanStep("invoke-my-own", childId)]),
            "alice", "acme"));

        reused.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task ACallerInAnotherTenant_CannotReferenceTheWorkflowEither()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var owned = await client.SendAsync(Submit(Definition([LlmStep("step")]), "alice", "acme"));
        var childId = await WorkflowIdOfAsync(owned);

        var crossTenant = await client.SendAsync(Submit(
            Definition([SubPlanStep("invoke-across-tenants", childId)]),
            "alice", "other-corp"));

        crossTenant.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
