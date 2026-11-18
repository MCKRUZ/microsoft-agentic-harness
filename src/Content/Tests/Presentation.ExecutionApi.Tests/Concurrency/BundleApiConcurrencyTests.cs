using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Presentation.ExecutionApi.Tests.Concurrency;

/// <summary>
/// Races the bundle API's admission controls through the real HTTP pipeline: many requests from one
/// caller arriving at the same instant, as a burst of client retries or a load test produces them.
/// </summary>
/// <remarks>
/// <para>
/// The store-level tests prove each guard is atomic in isolation. These prove the host keeps the same
/// promise when the requests come through routing, authentication, the rate limiter and MediatR together:
/// the cap admits exactly its limit, the excess is refused with the documented status, other callers are
/// unaffected, and no refusal ever surfaces as a server error. A 5xx under contention is the failure a
/// caller cannot act on, so each test counts them explicitly.
/// </para>
/// <para>
/// Every test uses its own caller id, so the per-caller rate-limit partitions never overlap between tests
/// sharing the class fixture. Bundle runs are started in streaming mode, which reserves a run without
/// executing it: the reservation stays active for the whole test, so the per-owner count cannot drop
/// mid-burst because a run finished.
/// </para>
/// </remarks>
[Trait("Category", "Concurrency")]
public sealed class BundleApiConcurrencyTests : IClassFixture<BundleApiConcurrencyTests.MultiCallerHost>
{
    private const string Tenant = "concurrency-tenant";
    private readonly HttpClient _client;

    public BundleApiConcurrencyTests(MultiCallerHost host) => _client = host.CreateClient();

    [Fact]
    public async Task StartRun_TwentyAtOnceForOneOwner_AcceptsExactlyTheCapAndRefusesTheRest()
    {
        // MaxActiveBundleRunsPerOwner defaults to 10. Twenty simultaneous starts must admit exactly ten:
        // one more means the cap is a check-then-act race; one fewer means the burst lost an admission.
        var owner = NewCaller();
        var handle = await RegisterAsync(owner);

        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ =>
            _client.SendAsync(StartStreamingRun(owner, handle))));

        Count(responses, HttpStatusCode.Accepted).Should().Be(10);
        Count(responses, HttpStatusCode.BadRequest).Should().Be(10, "being at capacity is the caller's own volume (400)");
        ServerErrors(responses).Should().Be(0);

        // The cap is per owner: a second caller is unaffected by the first caller's full allowance.
        var other = NewCaller();
        var otherHandle = await RegisterAsync(other);
        var otherStart = await _client.SendAsync(StartStreamingRun(other, otherHandle));
        otherStart.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task Register_ElevenAtOnce_AdmitsTheWindowAndRefusesTheRestWith429()
    {
        // The register policy allows ten per caller per minute. The eleventh simultaneous upload must be
        // refused by the limiter with the documented 429, not admitted and not failed with a 5xx.
        var owner = NewCaller();

        var responses = await Task.WhenAll(Enumerable.Range(0, 11).Select(_ =>
            _client.SendAsync(Register(owner))));

        Count(responses, HttpStatusCode.Created).Should().Be(10);
        Count(responses, HttpStatusCode.TooManyRequests).Should().Be(1);
        ServerErrors(responses).Should().Be(0);
    }

    [Fact]
    public async Task Poll_SixtyOneAtOnce_AdmitsTheWindowAndRefusesTheRestWith429()
    {
        // The default policy allows sixty requests per caller per minute. Polling a run that does not
        // exist needs no setup and answers 404, so every admitted request is distinguishable from a refusal.
        var owner = NewCaller();

        var responses = await Task.WhenAll(Enumerable.Range(0, 61).Select(_ =>
            _client.SendAsync(Authenticated(HttpMethod.Get, "/api/bundles/no-handle/runs/no-job", owner))));

        Count(responses, HttpStatusCode.NotFound).Should().Be(60);
        Count(responses, HttpStatusCode.TooManyRequests).Should().Be(1);
        ServerErrors(responses).Should().Be(0);
    }

    [Fact]
    public async Task Poll_OneCallerExhaustsItsWindow_AnotherCallerIsStillServed()
    {
        // Partitioning is per caller. A caller that spends its whole window must not starve anyone else,
        // which is the reason the limiter keys on the caller's stable id rather than one global window.
        var noisy = NewCaller();
        var quiet = NewCaller();

        await Task.WhenAll(Enumerable.Range(0, 61).Select(_ =>
            _client.SendAsync(Authenticated(HttpMethod.Get, "/api/bundles/no-handle/runs/no-job", noisy))));
        var quietPoll = await _client.SendAsync(
            Authenticated(HttpMethod.Get, "/api/bundles/no-handle/runs/no-job", quiet));

        quietPoll.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private static string NewCaller() => $"caller-{Guid.NewGuid():N}";

    private static int Count(IEnumerable<HttpResponseMessage> responses, HttpStatusCode status) =>
        responses.Count(r => r.StatusCode == status);

    private static int ServerErrors(IEnumerable<HttpResponseMessage> responses) =>
        responses.Count(r => (int)r.StatusCode >= 500);

    private async Task<string> RegisterAsync(string owner)
    {
        var response = await _client.SendAsync(Register(owner));
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Created, "registration failed with body: {0}", body);
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("handle").GetString()!;
    }

    private static HttpRequestMessage Register(string owner)
    {
        var request = Authenticated(HttpMethod.Post, "/api/bundles", owner);
        request.Content = new MultipartFormDataContent
        {
            { new ByteArrayContent(BuildValidBundleZip()), "file", "bundle.zip" }
        };
        return request;
    }

    private static HttpRequestMessage StartStreamingRun(string owner, string handle)
    {
        var request = Authenticated(HttpMethod.Post, $"/api/bundles/{handle}/runs", owner);
        request.Content = JsonContent.Create(new { userMessages = new[] { "hi" }, maxTurns = 1, stream = true });
        return request;
    }

    private static HttpRequestMessage Authenticated(HttpMethod method, string path, string owner)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add(HeaderIdentityAuthenticationHandler.UserHeader, owner);
        request.Headers.Add(HeaderIdentityAuthenticationHandler.TenantHeader, Tenant);
        return request;
    }

    private static byte[] BuildValidBundleZip()
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("AGENT.md");
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            writer.Write("---\nid: concurrency-bundle\nname: Concurrency Bundle\n---\nConcurrency bundle instructions.");
        }
        return ms.ToArray();
    }

    /// <summary>
    /// The shipped Execution API with the header identity scheme in place of anonymous access, so one host
    /// serves many distinct callers, each with its own rate-limit partition and per-owner run count.
    /// </summary>
    public sealed class MultiCallerHost : WebApplicationFactory<Program>
    {
        /// <inheritdoc />
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder) =>
            builder.ConfigureTestServices(services => services
                .AddAuthentication(HeaderIdentityAuthenticationHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, HeaderIdentityAuthenticationHandler>(
                    HeaderIdentityAuthenticationHandler.SchemeName, _ => { }));
    }
}
