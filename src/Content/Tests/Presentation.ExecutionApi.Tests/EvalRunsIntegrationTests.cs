using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Presentation.ExecutionApi.Controllers;
using Xunit;

namespace Presentation.ExecutionApi.Tests;

/// <summary>
/// Full-stack tests for the evaluation endpoints, driven through the real host.
/// </summary>
/// <remarks>
/// <para>
/// The properties worth proving here are the ones only the real pipeline can show: that the routes
/// exist and are mounted, that they are closed to unauthenticated callers, and — most importantly —
/// that a host which has not enabled evaluation serves nothing. Everything the surface can do costs
/// model spend on the host's own credentials, so "off unless an operator turned it on" is the claim
/// that has to hold against the composed host rather than against a handler in isolation.
/// </para>
/// <para>
/// This host ships with evaluation disabled, so these run against that default. The behaviour once it
/// is enabled — admission, ownership, reports — is covered against the real stores in
/// <c>EvalRunHandlerTests</c>, which can drive it without standing up an agent and a model provider.
/// </para>
/// </remarks>
public sealed class EvalRunsIntegrationTests
{
    private static WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services
                .AddAuthentication(HeaderIdentityAuthenticationHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, HeaderIdentityAuthenticationHandler>(
                    HeaderIdentityAuthenticationHandler.SchemeName, _ => { })));

    private static HttpRequestMessage Request(
        HttpMethod method, string url, object? body, string? oid, bool withRole = true)
    {
        var request = new HttpRequestMessage(method, url);
        if (body is not null)
            request.Content = JsonContent.Create(body);

        if (oid is not null)
        {
            request.Headers.Add(HeaderIdentityAuthenticationHandler.UserHeader, oid);
            request.Headers.Add(HeaderIdentityAuthenticationHandler.TenantHeader, "acme");

            if (withRole)
            {
                request.Headers.Add(
                    HeaderIdentityAuthenticationHandler.RolesHeader, EvalsController.ExecuteRole);
            }
        }

        return request;
    }

    [Fact]
    public async Task StartingARun_IsRefusedWhileEvaluationIsDisabled()
    {
        // The default this host ships with. An eval run spends real model budget on the host's own
        // credentials, so it must take a deliberate operator action to become reachable — not merely
        // an authenticated caller who knows the route.
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Request(
            HttpMethod.Post, "/api/evals/runs", new { datasets = new[] { "anything" } }, "alice"));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ReadingARun_IsRefusedWhileEvaluationIsDisabled()
    {
        // The read path is gated too. Left open it would report on runs from a period when evaluation
        // had been enabled, after an operator turned it off precisely to stop that.
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Request(
            HttpMethod.Get, "/api/evals/runs/some-job", body: null, "alice"));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ADisabledHostPublishesNoDatasets()
    {
        // The listing is deliberately not gated on Enabled — it answers "what could be run here" and
        // an empty answer is truthful and actionable. What must hold is that it discloses nothing: with
        // no roots configured there is no catalog, and it must not fall back to enumerating the
        // process's working directory.
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.SendAsync(
            Request(HttpMethod.Get, "/api/evals/datasets", body: null, "alice"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("datasets").EnumerateArray().Should().BeEmpty();
    }

    [Theory]
    [InlineData("GET", "/api/evals/datasets")]
    [InlineData("POST", "/api/evals/runs")]
    [InlineData("GET", "/api/evals/runs/some-job")]
    [InlineData("DELETE", "/api/evals/runs/some-job")]
    public async Task EveryEvaluationRouteRefusesAnUnauthenticatedCaller(string method, string url)
    {
        // Including the listing. A dataset name is a small disclosure, but it is the operator's
        // inventory and there is no reason an anonymous caller should have it.
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.SendAsync(
            Request(new HttpMethod(method), url, body: null, oid: null));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("GET", "/api/evals/datasets")]
    [InlineData("POST", "/api/evals/runs")]
    [InlineData("GET", "/api/evals/runs/some-job")]
    [InlineData("DELETE", "/api/evals/runs/some-job")]
    public async Task EveryEvaluationRouteRefusesAnAuthenticatedCallerWithoutTheRole(
        string method, string url)
    {
        // The case the role gate exists for. Every other route this host serves runs the caller's own
        // work under the caller's own grant; this one spends the host's model budget on the operator's
        // suites, which is a different kind of authority from holding a valid token. A caller who is
        // authenticated but not entitled must be refused — 403, not 401: they are who they say, they
        // just may not do this.
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.SendAsync(
            Request(new HttpMethod(method), url, body: null, oid: "alice", withRole: false));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
