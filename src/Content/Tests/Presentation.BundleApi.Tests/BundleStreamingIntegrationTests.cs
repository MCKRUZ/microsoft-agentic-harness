using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Presentation.BundleApi.Tests;

/// <summary>
/// Full-stack HTTP tests for the streaming surface, hosting the real composition via
/// <see cref="WebApplicationFactory{TEntryPoint}"/> under the shipped Development config (bundle execution
/// enabled, anonymous auth). They prove the stream endpoint's owner-scoping, the reserve-then-stream lifecycle
/// (a streaming run is created but not executed until the feed is opened), and that opening the feed drives the
/// run and emits AG-UI events — exercised on the fast path where the handle is gone, so no agent/LLM is needed.
/// </summary>
public sealed class BundleStreamingIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public BundleStreamingIntegrationTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public async Task Stream_UnknownRun_Returns404()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/bundles/nohandle/runs/nojob/stream");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task RunWithStream_Returns202_WithStreamUrl_AndReservationStaysQueuedUntilStreamed()
    {
        var client = _factory.CreateClient();
        var handle = await RegisterValidBundleAsync(client);

        var run = await client.PostAsJsonAsync(
            $"/api/bundles/{handle}/runs",
            new { userMessages = new[] { "hi" }, maxTurns = 1, stream = true });

        run.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var (jobId, streamUrl) = await ReadStartRunAsync(run);
        streamUrl.Should().Be($"/api/bundles/{handle}/runs/{jobId}/stream",
            "a streamed run must advertise its feed URL");

        // The reservation exists but has NOT been executed — its only driver is opening the stream, which this
        // test has not done. Polling must therefore still report it Queued.
        var poll = await client.GetAsync($"/api/bundles/{handle}/runs/{jobId}");
        poll.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await poll.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("status").GetString().Should().Be("Queued");
    }

    [Fact]
    public async Task Stream_DrivesRun_AndEmitsAgUiEvents_WhenHandleGone()
    {
        var client = _factory.CreateClient();
        var handle = await RegisterValidBundleAsync(client);

        var run = await client.PostAsJsonAsync(
            $"/api/bundles/{handle}/runs",
            new { userMessages = new[] { "hi" }, maxTurns = 1, stream = true });
        var (_, streamUrl) = await ReadStartRunAsync(run);

        // Delete the bundle so the run fails immediately when it tries to acquire the (now-gone) handle — this
        // drives the full stream plumbing to a terminal event without ever building an agent or calling an LLM.
        (await client.DeleteAsync($"/api/bundles/{handle}")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var stream = await client.GetAsync(streamUrl);

        stream.StatusCode.Should().Be(HttpStatusCode.OK);
        stream.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");
        var body = await stream.Content.ReadAsStringAsync();
        body.Should().Contain("RUN_STARTED").And.Contain("RUN_ERROR");
        body.Should().NotContain("RUN_FINISHED", "a run whose handle vanished did not complete successfully");
    }

    private static async Task<string> RegisterValidBundleAsync(HttpClient client)
    {
        using var content = new MultipartFormDataContent
        {
            { new ByteArrayContent(BuildValidBundleZip()), "file", "bundle.zip" }
        };

        var register = await client.PostAsync("/api/bundles", content);
        register.StatusCode.Should().Be(HttpStatusCode.Created,
            "a well-formed bundle must register; body was: {0}", await register.Content.ReadAsStringAsync());

        using var doc = JsonDocument.Parse(await register.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("handle").GetString()!;
    }

    private static async Task<(string JobId, string StreamUrl)> ReadStartRunAsync(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (doc.RootElement.GetProperty("jobId").GetString()!,
                doc.RootElement.GetProperty("streamUrl").GetString()!);
    }

    private static byte[] BuildValidBundleZip()
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("AGENT.md");
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            writer.Write("---\nid: stream-bundle\nname: Stream Bundle\n---\nStream bundle instructions.");
        }
        return ms.ToArray();
    }
}
