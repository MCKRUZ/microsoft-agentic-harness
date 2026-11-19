using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Presentation.ExecutionApi.Tests.Concurrency;

/// <summary>
/// The host-wide limit on live bundle streams, through the shipped HTTP pipeline: several callers, each within
/// their own allowance, together reaching the host's limit.
/// </summary>
/// <remarks>
/// <para>
/// Every live stream drives an agent conversation on its connection and never passes through the background
/// dispatcher, so without this limit the host's total is unbounded however small each caller's share is. The
/// per-caller limit alone cannot catch that: here no caller is ever over their own allowance.
/// </para>
/// <para>
/// A stream holds its permit only while its request is in flight, so each stream is parked in a test filter
/// that runs after the rate limiter and before the action. The limiter has already admitted it and keeps its
/// permit until the test lets it go, which makes "the host is full" a state the test controls rather than a
/// race against how fast a run finishes.
/// </para>
/// </remarks>
[Trait("Category", "Concurrency")]
public sealed class BundleStreamHostLimitTests : IClassFixture<BundleStreamHostLimitTests.LimitedHost>, IAsyncLifetime
{
    private const int HostLimit = 3;
    private const int PerCallerLimit = 2;
    private const string Tenant = "stream-limit-tenant";

    private readonly LimitedHost _host;
    private readonly HttpClient _client;
    private readonly List<Task<HttpResponseMessage>> _streams = [];

    public BundleStreamHostLimitTests(LimitedHost host)
    {
        _host = host;
        _client = host.CreateClient();
    }

    /// <inheritdoc />
    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>
    /// Opens the gate and waits for this test's streams to finish, so a test that fails part-way cannot leave
    /// streams holding the host's slots when the next test starts.
    /// </summary>
    public async Task DisposeAsync()
    {
        _host.Gate.Open();
        await Task.WhenAll(_streams).WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task HostFull_RefusesAnotherCallersStreamWith429_AndStillServesTheirOtherRequests()
    {
        _host.Gate.Block();
        var holders = Enumerable.Range(0, HostLimit).Select(_ => OpenStream(NewCaller())).ToList();
        await _host.Gate.WaitForParkedAsync(HostLimit);

        var latecomer = NewCaller();
        var refused = await AnsweredPromptly(OpenStream(latecomer));
        var poll = await _client.SendAsync(Authenticated(HttpMethod.Get, "/api/bundles/no-handle/runs/no-job", latecomer));

        refused.StatusCode.Should().Be(HttpStatusCode.TooManyRequests,
            "the host holds its limit of streams, though this caller holds none of them");
        poll.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "the host limit counts streams only, so a full host still answers the caller's other requests");

        _host.Gate.Open();
        var released = await Task.WhenAll(holders);
        released.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.NotFound,
            "each admitted stream reaches the action, which finds no such run; none is refused or fails");

        (await OpenStreamOnceTheHostHasRoom(latecomer)).StatusCode.Should().Be(HttpStatusCode.NotFound,
            "closing the streams gives their slots back");
    }

    [Fact]
    public async Task ACallerRefusedByTheirOwnLimit_DoesNotKeepAHostSlot()
    {
        // The host permit is taken before the per-caller one. If refusing the per-caller permit kept the host
        // permit, every refused attempt would shrink the host for everyone until the process restarted.
        _host.Gate.Block();
        var busy = NewCaller();
        var busyStreams = Enumerable.Range(0, PerCallerLimit).Select(_ => OpenStream(busy)).ToList();
        await _host.Gate.WaitForParkedAsync(PerCallerLimit);

        var overOwnLimit = await AnsweredPromptly(OpenStream(busy));
        overOwnLimit.StatusCode.Should().Be(HttpStatusCode.TooManyRequests, "the caller already holds their allowance");

        var other = OpenStream(NewCaller());
        await _host.Gate.WaitForParkedAsync(1);

        _host.Gate.Open();
        (await other).StatusCode.Should().Be(HttpStatusCode.NotFound,
            "the host had one slot left after the refused attempt, and this caller got it");
        await Task.WhenAll(busyStreams);
    }

    private static string NewCaller() => $"caller-{Guid.NewGuid():N}";

    /// <summary>
    /// A refusal is answered at once, while an admitted stream parks in the gate until the test opens it. A
    /// stream the limiter wrongly admits would otherwise hang the test until the client's 100-second timeout.
    /// </summary>
    private static Task<HttpResponseMessage> AnsweredPromptly(Task<HttpResponseMessage> response) =>
        response.WaitAsync(TimeSpan.FromSeconds(10));

    private Task<HttpResponseMessage> OpenStream(string caller)
    {
        var stream = _client.SendAsync(
            Authenticated(HttpMethod.Get, "/api/bundles/no-handle/runs/no-job/stream", caller),
            HttpCompletionOption.ResponseHeadersRead);
        _streams.Add(stream);
        return stream;
    }

    /// <summary>
    /// The rate limiter gives a permit back after the response is written, so the client can see a response a
    /// moment before its slot is free again. Retrying briefly absorbs that gap without hiding a slot that
    /// never comes back.
    /// </summary>
    private async Task<HttpResponseMessage> OpenStreamOnceTheHostHasRoom(string caller)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            var response = await OpenStream(caller);
            if (response.StatusCode != HttpStatusCode.TooManyRequests || DateTime.UtcNow > deadline)
                return response;
            await Task.Delay(50);
        }
    }

    private static HttpRequestMessage Authenticated(HttpMethod method, string path, string caller)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add(HeaderIdentityAuthenticationHandler.UserHeader, caller);
        request.Headers.Add(HeaderIdentityAuthenticationHandler.TenantHeader, Tenant);
        return request;
    }

    /// <summary>
    /// Parks requests to the bundle stream action until the test opens it. Runs after the rate limiter has
    /// admitted the request, so a parked request holds its stream permits.
    /// </summary>
    public sealed class StreamGate : IAsyncActionFilter
    {
        private readonly SemaphoreSlim _parked = new(0);
        private TaskCompletionSource _opened = Completed();

        /// <summary>
        /// Parks every stream request that arrives from now until <see cref="Open"/>, forgetting any request an
        /// earlier test left parked so its count cannot satisfy this test's wait.
        /// </summary>
        public void Block()
        {
            while (_parked.Wait(0))
            {
            }

            Volatile.Write(ref _opened, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        }

        /// <summary>Lets every parked request through, and every later one.</summary>
        public void Open() => Volatile.Read(ref _opened).TrySetResult();

        /// <summary>Waits until <paramref name="count"/> more requests have been parked.</summary>
        public async Task WaitForParkedAsync(int count)
        {
            for (var i = 0; i < count; i++)
                (await _parked.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeTrue("a stream the limiter admitted should reach the action");
        }

        /// <inheritdoc />
        public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
        {
            var opened = Volatile.Read(ref _opened).Task;
            if (!opened.IsCompleted
                && context.ActionDescriptor is ControllerActionDescriptor { ControllerName: "Bundles", ActionName: "Stream" })
            {
                _parked.Release();
                await opened.WaitAsync(context.HttpContext.RequestAborted);
            }

            await next();
        }

        private static TaskCompletionSource Completed()
        {
            var source = new TaskCompletionSource();
            source.SetResult();
            return source;
        }
    }

    /// <summary>
    /// The shipped Execution API with a small host stream limit, the header identity scheme so one host serves
    /// many callers, and <see cref="StreamGate"/> in the MVC filter pipeline.
    /// </summary>
    /// <remarks>
    /// The limits are set as environment variables because the stream limits are read from configuration while
    /// services are registered; <c>UseSetting</c> and <c>ConfigureAppConfiguration</c> apply too late for that
    /// read. This assembly runs its tests one at a time, so the variables cannot leak into another host's start.
    /// </remarks>
    public sealed class LimitedHost : WebApplicationFactory<Program>
    {
        private static readonly Dictionary<string, string> Limits = new()
        {
            ["AppConfig__AI__BundleExecution__MaxConcurrentStreams"] = HostLimit.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["AppConfig__AI__BundleExecution__MaxConcurrentStreamsPerCaller"] = PerCallerLimit.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

        /// <summary>Starts the host while the limit variables are visible, then clears them.</summary>
        public LimitedHost()
        {
            foreach (var (key, value) in Limits)
                Environment.SetEnvironmentVariable(key, value);
            try
            {
                _ = Server;
            }
            finally
            {
                foreach (var key in Limits.Keys)
                    Environment.SetEnvironmentVariable(key, null);
            }
        }

        /// <summary>The gate every stream request passes through.</summary>
        public StreamGate Gate { get; } = new();

        /// <inheritdoc />
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder) =>
            builder.ConfigureTestServices(services =>
            {
                services
                    .AddAuthentication(HeaderIdentityAuthenticationHandler.SchemeName)
                    .AddScheme<AuthenticationSchemeOptions, HeaderIdentityAuthenticationHandler>(
                        HeaderIdentityAuthenticationHandler.SchemeName, _ => { });
                services.Configure<MvcOptions>(options => options.Filters.Add(Gate));
            });
    }
}
