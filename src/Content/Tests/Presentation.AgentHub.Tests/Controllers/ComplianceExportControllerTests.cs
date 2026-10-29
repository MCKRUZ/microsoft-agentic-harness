using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Application.AI.Common.Interfaces.Agent;
using Application.Core.Compliance;
using Application.Core.CQRS.Compliance.ExportMyData;
using Domain.Common;
using Domain.Common.Compliance;
using Domain.Common.Models;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;
using Presentation.Common.Compliance;
using Xunit;

namespace Presentation.AgentHub.Tests.Controllers;

/// <summary>
/// <c>POST /api/compliance/export-my-data</c> (#902): a person's token gets their copy as a file that nothing caches; an
/// application's own token is refused, because every caller of that application shares its identity.
/// </summary>
/// <remarks>
/// <see cref="TestWebApplicationFactory"/> replaces the mediator host-wide, so these tests stub
/// <see cref="TestWebApplicationFactory.MockMediator"/> and prove what was, or was not, sent.
/// </remarks>
public sealed class ComplianceExportControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private const string Url = "/api/compliance/export-my-data";
    private readonly TestWebApplicationFactory _factory;
    private readonly ConcurrentQueue<AuditEntry> _records = new();

    /// <summary>Initialises the test class with the shared integration factory.</summary>
    public ComplianceExportControllerTests(TestWebApplicationFactory factory) => _factory = factory;

    private HttpClient Client(string? claimShape, string? userId = null)
    {
        // A user of its own per test: the per-person limit counts every request one user makes.
        var client = _factory.CreateAuthedClient(userId ?? $"caller-{Guid.NewGuid():N}", roles: null, services =>
            services.Replace(ServiceDescriptor.Singleton<IAuditSink>(new CapturingSink(_records))));
        if (claimShape is not null)
            client.DefaultRequestHeaders.Add(TestAuthHandler.ClaimShapeHeader, claimShape);
        return client;
    }

    private Func<bool> CaptureSent(Result<PersonalDataExport>? result = null)
    {
        var sent = false;
        _factory.MockMediator
            .Setup(m => m.Send(It.IsAny<ExportMyDataCommand>(), It.IsAny<CancellationToken>()))
            .Callback<IRequest<Result<PersonalDataExport>>, CancellationToken>((_, _) => sent = true)
            .ReturnsAsync(result ?? Result<PersonalDataExport>.Success(new PersonalDataExport(
                "export-1",
                new DateTimeOffset(2026, 10, 24, 18, 30, 5, TimeSpan.Zero),
                [new ExportManifestEntry("conversations", ExportStatus.Included, 1)],
                [new ExportSection("conversations", [JsonSerializer.SerializeToElement(new { id = "c-1" })])])));
        return () => sent;
    }

    [Theory]
    [InlineData("app-only")]
    [InlineData("app-idtyp")]
    public async Task ExportMyData_ApplicationToken_Returns403SendsNothingAndRecordsTheRefusal(string claimShape)
    {
        var sent = CaptureSent();
        using var client = Client(claimShape);

        var response = await client.PostAsync(Url, content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).Should().Contain("needs a person's sign-in");
        sent().Should().BeFalse("an application token must never reach the export");
        var record = _records.Should().ContainSingle().Subject;
        record.Outcome.Should().Be(AuditOutcome.Denied);
        record.FailureReason.Should().Be("application token");
    }

    [Fact]
    public async Task ExportMyData_APersonsToken_GetsTheCopyAsAFileNothingCaches()
    {
        var sent = CaptureSent();
        using var client = Client("person");

        var response = await client.PostAsync(Url, content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        sent().Should().BeTrue();
        response.Headers.CacheControl!.NoStore.Should().BeTrue("a cached copy would outlive the data it copies");
        response.Content.Headers.ContentDisposition!.DispositionType.Should().Be("attachment");
        response.Content.Headers.ContentDisposition.FileName!.Trim('"').Should().Be("personal-data-20261024T183005Z.json");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("sections")[0].GetProperty("records")[0].GetProperty("id").GetString().Should().Be("c-1");
        body.RootElement.GetProperty("manifest")[0].GetProperty("store").GetString().Should().Be("conversations");
    }

    [Fact]
    public async Task ExportMyData_TheCopyCannotBeMade_Returns500WithoutDetail()
    {
        CaptureSent(Result<PersonalDataExport>.Fail(AuditedExport.FailedMessage));
        using var client = Client("person");

        var response = await client.PostAsync(Url, content: null);

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        response.Content.Headers.ContentDisposition.Should().BeNull("a failure is not a file to save");
    }

    [Fact]
    public async Task ExportMyData_APersonAskingASixthTimeInAnHour_IsRefusedWith429()
    {
        CaptureSent();
        using var client = Client("person");
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 6; i++)
        {
            using var response = await client.PostAsync(Url, content: null);
            statuses.Add(response.StatusCode);
        }

        statuses.Take(5).Should().OnlyContain(s => s == HttpStatusCode.OK);
        statuses[5].Should().Be(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task ExportMyData_MoreThanTheHostsLimitAtOnce_TheExtraOneIsRefusedWith429()
    {
        // Each copy holds a person's whole data in memory; the per-person cap does not stop several people at once.
        var inFlight = 0;
        var allIn = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        _factory.MockMediator
            .Setup(m => m.Send(It.IsAny<ExportMyDataCommand>(), It.IsAny<CancellationToken>()))
            .Returns<IRequest<Result<PersonalDataExport>>, CancellationToken>(async (_, _) =>
            {
                if (Interlocked.Increment(ref inFlight) == ComplianceRateLimits.ConcurrentExportLimit)
                    allIn.TrySetResult();
                await release.Task;
                return Result<PersonalDataExport>.Success(new PersonalDataExport("e", DateTimeOffset.UnixEpoch, [], []));
            });
        using var client = Client("person");
        client.DefaultRequestHeaders.Remove(TestAuthHandler.UserIdHeader);

        var running = Enumerable.Range(0, ComplianceRateLimits.ConcurrentExportLimit)
            .Select(i => client.SendAsync(AsUser($"concurrent-{i}-{Guid.NewGuid():N}")))
            .ToList();
        await allIn.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var extra = client.SendAsync(AsUser($"concurrent-extra-{Guid.NewGuid():N}"));
        // Without the limit the extra request would wait behind the held ones forever; give it a bounded time to be refused.
        var refusedInTime = await Task.WhenAny(extra, Task.Delay(TimeSpan.FromSeconds(30))) == extra;
        release.SetResult();
        var finished = await Task.WhenAll(running);

        refusedInTime.Should().BeTrue("an export over the host's limit must be refused at once, not held until others finish");
        (await extra).StatusCode.Should().Be(HttpStatusCode.TooManyRequests, "another person's export was refused, not queued");
        finished.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.OK);
    }

    private static HttpRequestMessage AsUser(string userId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Url);
        request.Headers.Add(TestAuthHandler.UserIdHeader, userId);
        return request;
    }

    private sealed class CapturingSink(ConcurrentQueue<AuditEntry> records) : IAuditSink
    {
        public ValueTask RecordAsync(AuditEntry entry, CancellationToken cancellationToken = default)
        {
            if (entry.Action == AuditedExport.AuditAction)
                records.Enqueue(entry);
            return ValueTask.CompletedTask;
        }
    }
}
