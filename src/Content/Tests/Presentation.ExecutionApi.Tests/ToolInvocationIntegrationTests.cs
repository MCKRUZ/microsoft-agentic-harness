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
/// Full-stack tests for <c>POST /api/tools/{name}/invoke</c>, driven through the real host.
/// </summary>
/// <remarks>
/// <para>
/// This endpoint runs host-side code because a remote caller asked it to, so the properties worth
/// proving here are the ones only the composed host can show: that it is mounted, that it is closed to
/// unauthenticated callers, that holding a valid token is not by itself enough, and — above all — that
/// a host which has not switched direct invocation on runs nothing at all.
/// </para>
/// <para>
/// This host ships with direct invocation <em>disabled</em>, unlike bundle execution and workflow
/// submission which it enables. These tests run against that default deliberately: "off unless an
/// operator turned it on" is the claim that has to hold against the real container, and a test that
/// enabled it first would prove the opposite of what matters. The behaviour once it is enabled —
/// arming, governance, deadlines, sanitization — is covered against real collaborators in
/// <c>DirectToolInvokerTests</c>.
/// </para>
/// </remarks>
public sealed class ToolInvocationIntegrationTests
{
    private static WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services
                .AddAuthentication(HeaderIdentityAuthenticationHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, HeaderIdentityAuthenticationHandler>(
                    HeaderIdentityAuthenticationHandler.SchemeName, _ => { })));

    private static HttpRequestMessage Request(string? oid, bool withRole = true, string tool = "file_system")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/tools/{tool}/invoke")
        {
            Content = JsonContent.Create(new { operation = "read", parameters = new { path = "x" } })
        };

        if (oid is not null)
        {
            request.Headers.Add(HeaderIdentityAuthenticationHandler.UserHeader, oid);
            request.Headers.Add(HeaderIdentityAuthenticationHandler.TenantHeader, "acme");

            if (withRole)
            {
                request.Headers.Add(
                    HeaderIdentityAuthenticationHandler.RolesHeader, ToolsController.InvokeRole);
            }
        }

        return request;
    }

    [Fact]
    public async Task InvokingATool_IsRefusedWhileDirectInvocationIsDisabled()
    {
        // The default this host ships with, and the single most important assertion in this file. Every
        // other surface here runs the caller's own authored work; this one runs the host's own code on
        // the host's own resources, so it must take a deliberate operator action to become reachable —
        // not merely an authenticated caller holding the right role who knows the route.
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Request("alice"));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task InvokingATool_RefusesAnUnauthenticatedCaller()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Request(oid: null));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task InvokingATool_RefusesAnAuthenticatedCallerWithoutTheRole()
    {
        // The case the role gate exists for. Discovery is open to any authenticated caller because
        // listing a tool confers nothing; running one is a separate grant, and a caller who holds a
        // valid token but not this role must be refused — 403, not 401: they are who they say they are,
        // they just may not do this.
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Request("alice", withRole: false));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ListingTools_DoesNotRequireTheInvokeRole()
    {
        // The companion to the test above: the two surfaces are gated separately on purpose, so an
        // operator can hand out discovery without handing out execution. If listing silently required
        // the invoke role, that separation would exist only on paper.
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/tools");
        request.Headers.Add(HeaderIdentityAuthenticationHandler.UserHeader, "alice");

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ListingTools_AnswersWithACatalogEvenWhenTheEnvelopeGrantsNothing()
    {
        // The shipped default envelope grants no tools, so an empty array is the correct answer and
        // not an error — answering 403 instead would wrongly blame the caller's credential for what is
        // an operator configuration statement.
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/tools");
        request.Headers.Add(HeaderIdentityAuthenticationHandler.UserHeader, "alice");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        body.TryGetProperty("tools", out var tools).Should().BeTrue();
        tools.ValueKind.Should().Be(JsonValueKind.Array);
    }
}
