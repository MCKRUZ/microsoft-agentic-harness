using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Presentation.BundleApi.Tests;

/// <summary>
/// Full-stack HTTP tests for the bundle API, hosting the real composition via
/// <see cref="WebApplicationFactory{TEntryPoint}"/>. The host boots under the shipped Development config
/// (bundle execution enabled, anonymous auth), so these prove the entire pipeline — DI composition, auth,
/// controller, MediatR handlers, and the bundle stores — is wired correctly end to end.
/// </summary>
public sealed class BundlesControllerIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public BundlesControllerIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Host_Boots_AndUnknownRunReturns404()
    {
        // Proves the full composition boots (ValidateOnBuild) and an authenticated (anonymous-dev) request
        // reaches the controller and the real GetBundleRun handler, which reports an unknown run as 404.
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/bundles/nohandle/runs/nojob");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Register_WithNoFile_Returns400()
    {
        var client = _factory.CreateClient();

        // multipart with no file part
        using var content = new MultipartFormDataContent();
        var response = await client.PostAsync("/api/bundles", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Run_AgainstUnknownHandle_Returns404()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/bundles/does-not-exist/runs",
            new { userMessages = new[] { "hello" }, maxTurns = 1 });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Run_WithNoMessages_Returns400()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/bundles/whatever/runs",
            new { userMessages = Array.Empty<string>(), maxTurns = 1 });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Delete_UnknownHandle_Returns204_Idempotent()
    {
        var client = _factory.CreateClient();

        var response = await client.DeleteAsync("/api/bundles/never-existed");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Register_WithArchiveMissingAgentManifest_IsRejected()
    {
        // A zip with no AGENT.md trips a staging guard → the register command fails (not 201/500).
        var client = _factory.CreateClient();
        using var content = new MultipartFormDataContent
        {
            { new ByteArrayContent(BuildZip(("readme.txt", "not an agent"))), "file", "bundle.zip" }
        };

        var response = await client.PostAsync("/api/bundles", content);

        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().BeOneOf(
            [HttpStatusCode.BadRequest, HttpStatusCode.UnprocessableEntity],
            "staging guard rejection is a client error; body was: {0}", body);
    }

    private static byte[] BuildZip(params (string Name, string Content)[] entries)
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = archive.CreateEntry(name);
                using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
                writer.Write(content);
            }
        }
        return ms.ToArray();
    }
}
