using System.Net;
using System.Text;
using System.Text.Json;
using Application.AI.Common.Exceptions;
using Application.AI.Common.Interfaces.Resilience;
using Azure;
using Azure.AI.ContentSafety;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Identity;
using Domain.AI.Constants;
using Domain.AI.Resilience;
using Domain.Common.Config.AI.ContentSafety;
using Domain.Common.Config.AI.Resilience;
using FluentAssertions;
using Infrastructure.AI.ContentSafety;
using Infrastructure.AI.Tests.Resilience;
using Infrastructure.AI.Resilience;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.ContentSafety;

/// <summary>
/// <see cref="AzureContentSafetyService"/> (#904) against a stand-in for the Azure AI Content Safety endpoint: the real
/// SDK client, with the production client options (including its retry) and only the HTTP transport replaced, so the
/// request it sends, the response it parses and the way a failure reaches it are all as they are in a deployment.
/// </summary>
public sealed class AzureContentSafetyServiceTests
{
    private readonly StubHandler _handler = new();

    private ContentSafetyClient Client(TokenCredential? credential = null)
    {
        var options = Infrastructure.AI.DependencyInjection.CreateContentSafetyClientOptions();
        options.Transport = new HttpClientTransport(new HttpClient(_handler));
        options.Retry.Delay = TimeSpan.FromMilliseconds(1);
        var endpoint = new Uri("https://example.cognitiveservices.azure.com/");
        return credential is null
            ? new ContentSafetyClient(endpoint, new AzureKeyCredential("test-key"), options)
            : new ContentSafetyClient(endpoint, credential, options);
    }

    private AzureContentSafetyService Service(
        ContentSafetyConfig? config = null, TokenCredential? credential = null, IProviderErrorClassifier? errors = null) => new(
        Client(credential),
        config ?? new ContentSafetyConfig(),
        errors ?? ResilienceTestSupport.CreateClassifier(),
        NullLogger<AzureContentSafetyService>.Instance);

    private static string Analysis(params (string Category, int Severity)[] categories) => JsonSerializer.Serialize(new
    {
        blocklistsMatch = Array.Empty<object>(),
        categoriesAnalysis = categories.Select(c => new { category = c.Category, severity = c.Severity }),
    });

    private static readonly string Clean = Analysis(("Hate", 0), ("SelfHarm", 0), ("Sexual", 0), ("Violence", 0));

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

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task ScreenAsync_TheServiceFails_ReportsUnavailableNotABlock(HttpStatusCode status)
    {
        // An outage is not a verdict on the text: reporting it as a block would tell the caller the content was harmful
        // and count it as a violation. Whether to refuse or let it through is the pipeline's call, not this service's.
        _handler.Fallback = ("{\"error\":{\"code\":\"Failed\",\"message\":\"down\"}}", status);

        var act = async () => await Service().ScreenAsync("anything", CancellationToken.None);

        (await act.Should().ThrowAsync<ContentSafetyUnavailableException>())
            .Which.Message.Should().NotContain("anything", "the screened text never goes into the error");
    }

    [Fact]
    public async Task ScreenAsync_TheHostCannotBeReached_ReportsUnavailableAfterTheRetry()
    {
        // With the production retry, the SDK reports a failed connection as an aggregate of one failure per try. That
        // has to read as the outage it is, not as an unrecognised bug.
        _handler.Throw = new HttpRequestException(HttpRequestError.NameResolutionError, "No such host is known.");

        var act = async () => await Service().ScreenAsync("anything", CancellationToken.None);

        await act.Should().ThrowAsync<ContentSafetyUnavailableException>();
        _handler.Bodies.Should().HaveCount(2, "the production client retries once");
    }

    [Fact]
    public async Task ScreenAsync_AFailureThatIsNotAnOutage_PropagatesAsItIs()
    {
        // A bug is not an outage: calling it one would block every request as "unavailable", or, with unscreened content
        // allowed, let every request through unscreened, with the cause hidden behind the wrong label.
        _handler.Throw = new InvalidOperationException("A defect in the call, not the service.");

        var act = async () => await Service().ScreenAsync("anything", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task ScreenAsync_TheServiceFailsOnALongText_StopsAtTheFirstChunk()
    {
        // Once one chunk could not be screened, the outcome for the whole text is decided; calling a dead service for
        // every remaining chunk would only add a timeout per chunk.
        _handler.Fallback = ("{}", HttpStatusCode.ServiceUnavailable);
        var text = new string('a', (AzureContentSafetyService.MaxChunkLength * 2) + 10);

        var act = async () => await Service().ScreenAsync(text, CancellationToken.None);

        await act.Should().ThrowAsync<ContentSafetyUnavailableException>();
        _handler.Bodies.Should().HaveCount(2, "one chunk, tried twice");
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
    public async Task ScreenAsync_TheCredentialCannotBeObtained_ReportsUnavailable()
    {
        // A managed identity that is not assigned fails before any request is sent; that is still no screening.
        var act = async () => await Service(credential: new FailingCredential()).ScreenAsync("anything", CancellationToken.None);

        await act.Should().ThrowAsync<ContentSafetyUnavailableException>();
        _handler.Bodies.Should().BeEmpty();
    }

    [Fact]
    public async Task ScreenAsync_Cancelled_PropagatesRatherThanReportingAnOutage()
    {
        _handler.Respond(Clean);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var act = async () => await Service().ScreenAsync("anything", cancelled.Token);

        (await act.Should().ThrowAsync<OperationCanceledException>()).Which.Should().NotBeOfType<ContentSafetyUnavailableException>();
    }

    [Fact]
    public async Task ScreenAsync_ATimeoutThatRacedTheCallerCancelling_PropagatesTheCancellation()
    {
        // The classifier ranks a transport timeout above a caller cancellation, and rightly so for retries; but a request
        // nobody is waiting for any more is not an outage to count, and must not go on unscreened under a fail-open policy.
        using var cancelled = new CancellationTokenSource();
        _handler.OnSend = () => cancelled.Cancel();
        _handler.Throw = new TaskCanceledException("timed out", new TimeoutException());
        var transientForEverything = Mock.Of<IProviderErrorClassifier>(c =>
            c.Classify(It.IsAny<Exception>(), It.IsAny<CancellationToken>()) == ProviderFailureClassification.Transient);

        var act = async () => await Service(errors: transientForEverything).ScreenAsync("anything", cancelled.Token);

        (await act.Should().ThrowAsync<OperationCanceledException>()).Which.Should().NotBeOfType<ContentSafetyUnavailableException>();
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

    private sealed class FailingCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            throw new CredentialUnavailableException("No managed identity is assigned.");

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            throw new CredentialUnavailableException("No managed identity is assigned.");
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Queue<(string Body, HttpStatusCode Status)> _responses = new();

        public List<string> Bodies { get; } = [];

        /// <summary>The response once the queue is empty.</summary>
        public (string Body, HttpStatusCode Status) Fallback { get; set; } = ("{}", HttpStatusCode.OK);

        /// <summary>When set, every request fails with this exception instead of returning a response.</summary>
        public Exception? Throw { get; set; }

        /// <summary>Runs as each request arrives, before it is answered.</summary>
        public Action? OnSend { get; set; }

        public void Respond(string body, HttpStatusCode status = HttpStatusCode.OK) => _responses.Enqueue((body, status));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            OnSend?.Invoke();
            if (Throw is not null)
                throw Throw;
            var (body, status) = _responses.Count > 0 ? _responses.Dequeue() : Fallback;
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
