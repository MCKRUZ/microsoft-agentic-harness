using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Presentation.AgentHub.Controllers;
using System.Net;
using Xunit;

namespace Presentation.AgentHub.Tests.Controllers;

/// <summary>
/// Covers how <c>GET /api/sessions</c> treats the <c>status</c> query parameter.
/// </summary>
/// <remarks>
/// <para>
/// The filter used to be a raw string passed straight into the SQL predicate. Any word at all was
/// accepted and answered 200 with an empty list, which reads to a caller as "there are no sessions in
/// that state" — indistinguishable from "that state does not exist". That is precisely the confusion
/// #289 lived inside: the hub was writing <c>"errored"</c>, the schema was rejecting it, and a
/// dashboard querying <c>?status=errored</c> would have reported a clean, empty result either way.
/// </para>
/// <para>
/// The numeric and comma cases are here because <see cref="Enum.TryParse{TEnum}(string?, bool, out TEnum)"/>
/// accepts both — <c>"1"</c> becomes a member by ordinal and <c>"Active,Error"</c> becomes a bitwise OR
/// — so a parser built on it would turn a nonsense query into a real filter. The controller uses
/// <c>EnumNameHelper</c>, which refuses both, and these tests fail if anyone swaps it back.
/// </para>
/// </remarks>
public sealed class SessionsControllerStatusFilterTests
    : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    /// <summary>Initialises the test class with the shared integration factory.</summary>
    public SessionsControllerStatusFilterTests(TestWebApplicationFactory factory) =>
        _factory = factory;

    /// <summary>Creates an HTTP client holding the observer role, so only the filter is under test.</summary>
    private HttpClient CreateObserverClient()
    {
        var client = _factory
            .WithWebHostBuilder(b => b.ConfigureTestServices(services =>
            {
                services.AddAuthentication(TestAuthHandler.SchemeName)
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                        TestAuthHandler.SchemeName, _ => { });
            }))
            .CreateClient();

        client.DefaultRequestHeaders.Add(TestAuthHandler.UserIdHeader, "observer-user");
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, SessionsController.ObserverRole);
        return client;
    }

    [Theory]
    [InlineData("active")]
    [InlineData("completed")]
    [InlineData("error")]
    [InlineData("Error")]
    [InlineData("ERROR")]
    // 'cancelled' moved here from the rejected set when #301 gave it a state. The controller needed
    // no change to accept it: the filter is parsed out of SessionStatus, so widening the enum widened
    // the API. That is the design working, and it is why this case is worth keeping on both sides of
    // the move rather than deleting — a regression to a hardcoded list would land right here.
    [InlineData("cancelled")]
    public async Task GetSessions_StatusTheSchemaCanHold_Returns200(string status)
    {
        using var client = CreateObserverClient();

        var response = await client.GetAsync($"/api/sessions?status={status}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// <c>errored</c> is named explicitly rather than covered by a generic "garbage" case: it is one
    /// of the two words #289's callers were actually writing, so a regression that reinstated string
    /// passthrough would show up here by name.
    /// </summary>
    /// <remarks>
    /// Its companion, <c>cancelled</c>, has moved to the accepted set. It was rejected here only
    /// because the schema had no word for it and the template could not deliver one to an existing
    /// database; #301 fixed that, and this is the seam where the change becomes visible to a caller.
    /// </remarks>
    [Theory]
    [InlineData("errored")]
    [InlineData("nonsense")]
    [InlineData("1")]
    [InlineData("-1")]
    [InlineData("active,error")]
    public async Task GetSessions_StatusTheSchemaCannotHold_Returns400(string status)
    {
        using var client = CreateObserverClient();

        var response = await client.GetAsync($"/api/sessions?status={Uri.EscapeDataString(status)}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>
    /// The 400 body names the value it rejected, but does not repeat an unbounded amount of it.
    /// </summary>
    /// <remarks>
    /// An error body is the cheapest place to make a service echo caller-supplied text back at the
    /// caller and into every log that records the response. The bound is asserted rather than trusted
    /// because it is one line of code that nothing else would notice the loss of.
    /// </remarks>
    [Fact]
    public async Task GetSessions_OverlongStatus_IsRejectedWithoutEchoingAllOfIt()
    {
        using var client = CreateObserverClient();
        var overlong = new string('z', 5_000);

        var response = await client.GetAsync($"/api/sessions?status={overlong}");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        body.Should().NotContain(overlong);
        body.Length.Should().BeLessThan(1_000, "the rejected value must be truncated, not repeated");
    }

    [Fact]
    public async Task GetSessions_NoStatus_Returns200AndDoesNotFilter()
    {
        using var client = CreateObserverClient();

        var response = await client.GetAsync("/api/sessions");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// An empty or whitespace value is "no filter", not a bad filter — a UI that binds a dropdown
    /// straight to the query string sends <c>?status=</c> for its "All" option, and that must not be
    /// a 400.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("%20")]
    public async Task GetSessions_BlankStatus_IsTreatedAsNoFilter(string status)
    {
        using var client = CreateObserverClient();

        var response = await client.GetAsync($"/api/sessions?status={status}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
