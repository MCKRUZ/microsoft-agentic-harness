using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using FluentAssertions;
using Infrastructure.AI.KnowledgeGraph.Scoping;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Presentation.BundleApi.Services;
using Xunit;

namespace Presentation.BundleApi.Tests;

/// <summary>
/// Proves the bundle API host establishes a per-caller knowledge scope. This host persists plan state,
/// and <c>PlannerScopeFilter.VisibleTo</c> treats a <c>null</c> owner as <em>global</em> — readable by
/// every caller in every tenant. So an execution host that never sets scope does not write "private"
/// records, it writes world-readable ones. These tests hold that door shut from two sides: an
/// authenticated caller gets their own scope, and a caller with no identity never reaches the pipeline
/// at all.
/// </summary>
public sealed class KnowledgeScopePipelineTests
{
    /// <summary>
    /// Serializes environment-variable overrides across factory startups, matching the pattern in
    /// <c>McpServerAuthFailClosedTests</c>: env vars are the only default configuration source that both
    /// outranks appsettings.json and is visible to the eager <c>builder.Configuration</c> read inside
    /// <c>AddBundleApiServices</c> — <c>ConfigureAppConfiguration</c> overrides apply too late for it.
    /// </summary>
    private static readonly object EnvironmentLock = new();

    [Fact]
    public async Task AuthenticatedRequest_EstablishesKnowledgeScopeForTheCaller()
    {
        var recorder = new ScopeRecorder();
        using var factory = CreateAuthenticatedFactory(recorder);
        using var client = factory.CreateClient();

        var response = await SubmitRunAsync(client, oid: "alice", tid: "acme");

        // The handle does not exist, so the request reaches the real handler and 404s — which is the
        // point: it traversed the whole pipeline, scope middleware included.
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        recorder.Observed.Should().ContainSingle()
            .Which.Should().Be(("alice", "acme"),
                "the scope middleware must map the authenticated principal onto the request's knowledge scope");
    }

    [Fact]
    public async Task TwoCallers_GetDistinctNonNullScopes()
    {
        var recorder = new ScopeRecorder();
        using var factory = CreateAuthenticatedFactory(recorder);
        using var client = factory.CreateClient();

        await SubmitRunAsync(client, oid: "alice", tid: "acme");
        await SubmitRunAsync(client, oid: "bob", tid: "acme");

        recorder.Observed.Should().HaveCount(2);
        recorder.Observed.Should().OnlyContain(o => o.UserId != null,
            "a null owner is global — every caller must carry a real owner for plan isolation to bite");
        recorder.Observed[0].UserId.Should().NotBe(recorder.Observed[1].UserId,
            "each caller's plans must be stamped to that caller, not to a shared identity");
    }

    [Fact]
    public async Task SubOnlyCaller_EstablishesScope_JustLikeAnOidCaller()
    {
        // oid is an Entra-ism; plenty of OIDC providers issue only sub. While the scope resolver required
        // oid, such a caller earned bundle ownership but got a NULL knowledge scope — a world-readable
        // global plan. Bundle ownership and knowledge scope now share one resolver.
        var recorder = new ScopeRecorder();
        using var factory = CreateAuthenticatedFactory(recorder);
        using var client = factory.CreateClient();

        var response = await SubmitRunAsync(client, subjectHeader: HeaderIdentityAuthenticationHandler.SubHeader,
            id: "subject-only", tid: "acme");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "a sub-only caller must be accepted as an owner, not rejected as identity-less");
        recorder.Observed.Should().ContainSingle()
            .Which.Should().Be(("subject-only", "acme"));
    }

    [Fact]
    public async Task AnonymousDevMode_EstablishesAStableScope_NotAGlobalNullOwner()
    {
        // The shipped Development config opts into anonymous auth. Its synthetic principal now carries a
        // stable oid, so plans written on a developer's machine are owned rather than stamped global.
        var recorder = new ScopeRecorder();
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => AddScopeRecorder(services, recorder)));
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/bundles/does-not-exist/runs",
            new { userMessages = new[] { "hello" }, maxTurns = 1 });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        recorder.Observed.Should().ContainSingle()
            .Which.UserId.Should().Be(AnonymousAuthenticationHandler.AnonymousUserId,
                "a null owner would be GLOBAL; the anonymous principal must resolve to a real, stable id");
    }

    [Fact]
    public async Task RequestWithoutIdentity_IsRejected_AndNeverEstablishesScope()
    {
        // Boot the host with a real Entra scheme (no anonymous opt-in), the production posture. A caller
        // with no token must be turned away by authentication rather than falling through as an
        // identity-less caller whose plans would persist with a null (global) owner.
        //
        // SCOPE OF THIS TEST — read before relying on it. It pins the PROPERTY "no identity => rejected",
        // not any one mechanism. Two independent layers uphold that property: the controller's [Authorize]
        // (plus the scheme's FallbackPolicy) and BundlesController.ResolveCallerId returning 401 on a null
        // stable id. Mutation testing confirmed it stays green when EITHER layer alone is removed, and only
        // fails when both are. So do NOT read a passing run here as evidence that [Authorize] is intact —
        // if you are changing one of those two layers, it needs its own test.
        var recorder = new ScopeRecorder();
        using var factory = CreateEntraConfiguredFactory(recorder);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/bundles/does-not-exist/runs",
            new { userMessages = new[] { "hello" }, maxTurns = 1 });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        recorder.Observed.Should().BeEmpty(
            "an unauthenticated caller must never establish a scope, and must never reach plan persistence");
    }

    // -- Helpers --

    private static Task<HttpResponseMessage> SubmitRunAsync(HttpClient client, string oid, string tid) =>
        SubmitRunAsync(client, HeaderIdentityAuthenticationHandler.UserHeader, oid, tid);

    private static async Task<HttpResponseMessage> SubmitRunAsync(
        HttpClient client, string subjectHeader, string id, string tid)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/bundles/does-not-exist/runs")
        {
            Content = JsonContent.Create(new { userMessages = new[] { "hello" }, maxTurns = 1 })
        };
        request.Headers.Add(subjectHeader, id);
        request.Headers.Add(HeaderIdentityAuthenticationHandler.TenantHeader, tid);
        return await client.SendAsync(request);
    }

    /// <summary>
    /// Boots the host under its shipped Development config (anonymous opt-in) but replaces the default
    /// authentication scheme with one that mints a principal from request headers, so a single host can
    /// serve several distinct callers.
    /// </summary>
    private static WebApplicationFactory<Program> CreateAuthenticatedFactory(ScopeRecorder recorder) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services
                    .AddAuthentication(HeaderIdentityAuthenticationHandler.SchemeName)
                    .AddScheme<AuthenticationSchemeOptions, HeaderIdentityAuthenticationHandler>(
                        HeaderIdentityAuthenticationHandler.SchemeName, _ => { });
                AddScopeRecorder(services, recorder);
            }));

    private static WebApplicationFactory<Program> CreateEntraConfiguredFactory(ScopeRecorder recorder)
    {
        var variables = new Dictionary<string, string?>
        {
            ["AppConfig__AI__BundleExecution__Auth__TenantId"] = "11111111-1111-1111-1111-111111111111",
            ["AppConfig__AI__BundleExecution__Auth__ClientId"] = "22222222-2222-2222-2222-222222222222",
            ["AppConfig__AI__BundleExecution__Auth__AllowAnonymous"] = "false",
        };

        lock (EnvironmentLock)
        {
            foreach (var (key, value) in variables)
                Environment.SetEnvironmentVariable(key, value);

            var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
                builder.ConfigureTestServices(services => AddScopeRecorder(services, recorder)));

            try
            {
                _ = factory.Server; // Force startup while the overrides are visible.
                return factory;
            }
            catch
            {
                factory.Dispose();
                throw;
            }
            finally
            {
                foreach (var key in variables.Keys)
                    Environment.SetEnvironmentVariable(key, null);
            }
        }
    }

    /// <summary>
    /// Wraps the production <see cref="KnowledgeScopeAccessor"/> so the test can observe what the
    /// pipeline established. The read side is still the real accessor: what is recorded is what a
    /// downstream store would see, not merely what the middleware passed in.
    /// </summary>
    private static void AddScopeRecorder(IServiceCollection services, ScopeRecorder recorder)
    {
        services.AddSingleton(recorder);
        services.AddScoped<IKnowledgeScopeWriter>(sp => new RecordingScopeWriter(
            sp.GetRequiredService<KnowledgeScopeAccessor>(),
            sp.GetRequiredService<ScopeRecorder>()));
    }

    private sealed class ScopeRecorder
    {
        private readonly ConcurrentQueue<(string? UserId, string? TenantId)> _observed = new();

        public void Record(string? userId, string? tenantId) => _observed.Enqueue((userId, tenantId));

        public IReadOnlyList<(string? UserId, string? TenantId)> Observed => [.. _observed];
    }

    private sealed class RecordingScopeWriter : IKnowledgeScopeWriter
    {
        private readonly KnowledgeScopeAccessor _inner;
        private readonly ScopeRecorder _recorder;

        public RecordingScopeWriter(KnowledgeScopeAccessor inner, ScopeRecorder recorder)
        {
            _inner = inner;
            _recorder = recorder;
        }

        public IDisposable SetScope(
            string? userId = null,
            string? tenantId = null,
            string? datasetId = null,
            string? datasetName = null,
            string? datasetOwnerId = null)
        {
            var token = _inner.SetScope(userId, tenantId, datasetId, datasetName, datasetOwnerId);
            _recorder.Record(_inner.UserId, _inner.TenantId);
            return token;
        }
    }

    /// <summary>
    /// Test authentication scheme that mints a principal from request headers, so one host can serve
    /// several distinct callers. Returns <c>NoResult</c> when no header is present, which is what makes
    /// the "no identity" case a genuine 401 rather than a rigged one.
    /// </summary>
    private sealed class HeaderIdentityAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public const string SchemeName = "TestIdentity";
        public const string UserHeader = "X-Test-Oid";
        public const string SubHeader = "X-Test-Sub";
        public const string TenantHeader = "X-Test-Tid";

        public HeaderIdentityAuthenticationHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var oid = Request.Headers[UserHeader].ToString();
            var sub = Request.Headers[SubHeader].ToString();
            if (string.IsNullOrEmpty(oid) && string.IsNullOrEmpty(sub))
                return Task.FromResult(AuthenticateResult.NoResult());

            var claims = new List<Claim>();
            if (!string.IsNullOrEmpty(oid))
                claims.Add(new Claim("oid", oid));
            if (!string.IsNullOrEmpty(sub))
                claims.Add(new Claim("sub", sub));

            var tid = Request.Headers[TenantHeader].ToString();
            if (!string.IsNullOrEmpty(tid))
                claims.Add(new Claim("tid", tid));

            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
        }
    }
}
