using System.Net;
using System.Text;
using System.Text.Json;
using Azure;
using Azure.AI.ContentSafety;
using Azure.Core.Pipeline;
using Application.AI.Common.Exceptions;
using Domain.AI.Constants;
using Domain.Common.Config.AI.ContentSafety;
using FluentAssertions;
using Infrastructure.AI.ContentSafety;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Infrastructure.AI.Tests.ContentSafety;

/// <summary>
/// <see cref="AzureContentSafetyService"/> (#904) against a stand-in for the Azure AI Content Safety endpoint: the real
/// SDK client, with only the HTTP transport replaced, so the request it sends and the response it parses are the wire
/// format.
/// </summary>
public sealed class AzureContentSafetyServiceTests
{
    private readonly StubHandler _handler = new();

    private AzureContentSafetyService Service(ContentSafetyConfig? config = null)
    {
        var options = new ContentSafetyClientOptions { Transport = new HttpClientTransport(new HttpClient(_handler)) };
        options.Retry.MaxRetries = 0;
        var client = new ContentSafetyClient(
            new Uri("https://example.cognitiveservices.azure.com/"), new AzureKeyCredential("test-key"), options);
        return new AzureContentSafetyService(
            client, config ?? new ContentSafetyConfig(), NullLogger<AzureContentSafetyService>.Instance);
    }

    private static string Analysis(params (string Category, int Severity)[] categories) => JsonSerializer.Serialize(new
    {
        blocklistsMatch = Array.Empty<object>(),
        categoriesAnalysis = categories.Select(c => new { category = c.Category, severity = c.Severity }),
    });

    [Fact]
    public async Task ScreenAsync_EveryCategoryBelowTheThreshold_Passes()
    {
        _handler.Respond(Analysis(("Hate", 2), ("SelfHarm", 0), ("Sexual", 0), ("Violence", 2)));

        var result = await Service().ScreenAsync("an ordinary request", CancellationToken.None);

        result.IsBlocked.Should().BeFalse();
    }

    [Fact]
    public async Task ScreenAsync_ACategoryExactlyAtTheThreshold_Blocks()
    {
        // The threshold is inclusive: "block at 4" blocks a 4, not only what is above it.
        _handler.Respond(Analysis(("Hate", 4), ("SelfHarm", 0), ("Sexual", 0), ("Violence", 2)));

        var result = await Service().ScreenAsync("a harmful request", CancellationToken.None);

        result.IsBlocked.Should().BeTrue();
        result.Category.Should().Be(SafetyCategories.Hate);
    }

    [Fact]
    public async Task ScreenAsync_SeveralCategoriesOverTheThreshold_BlocksNamingTheWorst()
    {
        _handler.Respond(Analysis(("Hate", 4), ("SelfHarm", 0), ("Sexual", 0), ("Violence", 6)));

        var result = await Service().ScreenAsync("a harmful request", CancellationToken.None);

        result.IsBlocked.Should().BeTrue();
        result.Category.Should().Be(SafetyCategories.Violence, "the highest severity at or over the threshold is the one reported");
        result.BlockReason.Should().Contain(SafetyCategories.Violence);
    }

    [Theory]
    [InlineData("Hate", SafetyCategories.Hate)]
    [InlineData("SelfHarm", SafetyCategories.SelfHarm)]
    [InlineData("Sexual", SafetyCategories.Sexual)]
    [InlineData("Violence", SafetyCategories.Violence)]
    public void HarnessCategory_IsTheHarnessNameNotTheServiceName(string service, string harness)
    {
        // The block's category reaches metrics, safety events and exceptions; dashboards key on the harness's names.
        AzureContentSafetyService.HarnessCategory(new TextCategory(service)).Should().Be(harness);
    }

    [Fact]
    public async Task ScreenAsync_SendsTheTextAndOnlyTheConfiguredCategories()
    {
        _handler.Respond(Analysis(("Hate", 0)));

        await Service(new ContentSafetyConfig { Categories = ["Hate"] }).ScreenAsync("check this", CancellationToken.None);

        var body = JsonDocument.Parse(_handler.Bodies.Single()).RootElement;
        body.GetProperty("text").GetString().Should().Be("check this");
        body.GetProperty("categories").EnumerateArray().Select(c => c.GetString()).Should().Equal("Hate");
    }

    [Fact]
    public async Task ScreenAsync_ServiceFails_RefusesAsUnavailableNotAsABlock()
    {
        // A screen that lets everything through while it is down is no screen. But an outage is not a verdict on the
        // text: reporting it as a block would tell the caller the content was harmful and count it as a violation.
        _handler.Respond("{\"error\":{\"code\":\"InternalServerError\",\"message\":\"down\"}}", HttpStatusCode.InternalServerError);

        var act = async () => await Service().ScreenAsync("anything", CancellationToken.None);

        (await act.Should().ThrowAsync<ContentSafetyUnavailableException>())
            .Which.Message.Should().NotContain("anything", "the screened text never goes into the error");
    }

    [Fact]
    public async Task ScreenAsync_ServiceFailsAndTheDeploymentAllowsUnscreened_PassesWithoutCallingAgain()
    {
        // Once one chunk could not be screened, the outcome for the whole text is decided; calling a dead service for
        // every remaining chunk would only add a timeout per chunk.
        _handler.Respond("{}", HttpStatusCode.ServiceUnavailable);
        var text = new string('a', (AzureContentSafetyService.MaxChunkLength * 2) + 10);

        var result = await Service(new ContentSafetyConfig { BlockWhenUnavailable = false })
            .ScreenAsync(text, CancellationToken.None);

        result.IsBlocked.Should().BeFalse();
        _handler.Bodies.Should().ContainSingle();
    }

    [Fact]
    public async Task ScreenAsync_TextOverTheServiceLimit_IsScreenedInChunksAndStopsAtTheFirstBlock()
    {
        _handler.Respond(Analysis(("Hate", 0)));
        _handler.Respond(Analysis(("Hate", 6)));
        _handler.Respond(Analysis(("Hate", 0)));
        var text = new string('a', (AzureContentSafetyService.MaxChunkLength * 2) + 10);

        var result = await Service(new ContentSafetyConfig { Categories = ["Hate"] }).ScreenAsync(text, CancellationToken.None);

        result.IsBlocked.Should().BeTrue();
        _handler.Bodies.Should().HaveCount(2, "screening stops at the first chunk that blocks");
        _handler.Bodies.Select(b => JsonDocument.Parse(b).RootElement.GetProperty("text").GetString()!.Length)
            .Should().OnlyContain(length => length <= AzureContentSafetyService.MaxChunkLength);
    }

    [Fact]
    public void Chunks_APassageStraddlingACut_LiesWholeInOneChunk()
    {
        // With back-to-back chunks, padding a message so a harmful sentence crosses a cut gets each half scored without
        // the other. Every passage up to the overlap long has to reach the service in one piece, wherever it sits.
        var random = new Random(42);
        var text = new string([.. Enumerable.Range(0, (AzureContentSafetyService.MaxChunkLength * 3) + 77)
            .Select(_ => (char)('a' + random.Next(26)))]);

        var chunks = AzureContentSafetyService.Chunks(text).ToList();

        chunks.Should().OnlyContain(chunk => chunk.Length <= AzureContentSafetyService.MaxChunkLength);
        chunks[0].Should().Be(text[..chunks[0].Length]);
        text.Should().EndWith(chunks[^1]);
        for (var start = 0; start + AzureContentSafetyService.ChunkOverlap <= text.Length; start += 97)
        {
            var passage = text.Substring(start, AzureContentSafetyService.ChunkOverlap);
            chunks.Should().Contain(chunk => chunk.Contains(passage, StringComparison.Ordinal), $"the passage at {start} is cut");
        }
    }

    [Fact]
    public void Chunks_NeverSplitASurrogatePair()
    {
        var emoji = "\U0001F600";
        var text = new string('a', AzureContentSafetyService.MaxChunkLength - 1) + emoji
            + new string('b', AzureContentSafetyService.ChunkOverlap - 1) + emoji + "tail";

        var chunks = AzureContentSafetyService.Chunks(text).ToList();

        chunks.Should().HaveCountGreaterThan(1);
        chunks.Should().OnlyContain(c => !char.IsHighSurrogate(c[c.Length - 1]) && !char.IsLowSurrogate(c[0]));
        chunks[0].Should().Be(text[..chunks[0].Length]);
        text.Should().EndWith(chunks[^1]);
    }

    [Fact]
    public void Chunks_TextWithinTheLimit_IsSentAsIs()
    {
        AzureContentSafetyService.Chunks("short").Should().Equal("short");
        AzureContentSafetyService.Chunks(string.Empty).Should().Equal(string.Empty);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"blocklistsMatch\":[],\"categoriesAnalysis\":[]}")]
    [InlineData("{\"blocklistsMatch\":[],\"categoriesAnalysis\":[{\"category\":\"Hate\",\"severity\":0}]}")]
    [InlineData("{\"blocklistsMatch\":[],\"categoriesAnalysis\":[{\"category\":\"Hate\"},{\"category\":\"SelfHarm\",\"severity\":0},{\"category\":\"Sexual\",\"severity\":0},{\"category\":\"Violence\",\"severity\":0}]}")]
    public async Task ScreenAsync_AResponseThatDoesNotScoreEveryRequestedCategory_IsTreatedAsUnavailable(string body)
    {
        // A category that came back unscored was not screened, however well-formed the rest of the response is.
        _handler.Respond(body);

        var act = async () => await Service().ScreenAsync("anything", CancellationToken.None);

        await act.Should().ThrowAsync<ContentSafetyUnavailableException>();
    }

    [Fact]
    public async Task ScreenAsync_TheCredentialCannotBeObtained_RefusesAsUnavailable()
    {
        // A managed identity that is not assigned fails before any request is sent; that is still no screening.
        var options = new ContentSafetyClientOptions { Transport = new HttpClientTransport(new HttpClient(_handler)) };
        options.Retry.MaxRetries = 0;
        var client = new ContentSafetyClient(
            new Uri("https://example.cognitiveservices.azure.com/"), new FailingCredential(), options);
        var service = new AzureContentSafetyService(
            client, new ContentSafetyConfig(), NullLogger<AzureContentSafetyService>.Instance);

        var act = async () => await service.ScreenAsync("anything", CancellationToken.None);

        await act.Should().ThrowAsync<ContentSafetyUnavailableException>();
        _handler.Bodies.Should().BeEmpty();
    }

    [Fact]
    public void ClientOptions_FailWithinSecondsRatherThanTheSdkDefaults()
    {
        // Screening sits in front of every turn; the SDK's three retries and 100-second timeout per call would stall a
        // long message for minutes while the service hangs.
        var options = Infrastructure.AI.DependencyInjection.CreateContentSafetyClientOptions();

        options.Retry.MaxRetries.Should().Be(1);
        options.Retry.NetworkTimeout.Should().Be(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task ScreenAsync_Cancelled_PropagatesRatherThanBlocking()
    {
        _handler.Respond(Analysis(("Hate", 0)));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var act = async () => await Service().ScreenAsync("anything", cancelled.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private sealed class FailingCredential : Azure.Core.TokenCredential
    {
        public override Azure.Core.AccessToken GetToken(Azure.Core.TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            throw new Azure.Identity.CredentialUnavailableException("No managed identity is assigned.");

        public override ValueTask<Azure.Core.AccessToken> GetTokenAsync(Azure.Core.TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            throw new Azure.Identity.CredentialUnavailableException("No managed identity is assigned.");
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Queue<(string Body, HttpStatusCode Status)> _responses = new();

        public List<string> Bodies { get; } = [];

        public void Respond(string body, HttpStatusCode status = HttpStatusCode.OK) => _responses.Enqueue((body, status));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            var (body, status) = _responses.Count > 0 ? _responses.Dequeue() : ("{}", HttpStatusCode.OK);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
