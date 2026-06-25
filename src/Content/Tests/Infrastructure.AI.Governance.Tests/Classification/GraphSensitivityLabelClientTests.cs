using System.Net;
using System.Text;
using Azure.Core;
using Domain.AI.Governance;
using Domain.Common.Config.AI.Governance;
using FluentAssertions;
using Infrastructure.AI.Governance.Classification;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Infrastructure.AI.Governance.Tests.Classification;

/// <summary>
/// Tests for <see cref="GraphSensitivityLabelClient"/> against a stubbed Microsoft Graph endpoint.
/// They pin the honest contract of the MIP provider: it maps an embedded label id to the tenant label
/// taxonomy, returns Unknown (without a network call) when no id is present, caches the taxonomy, and
/// surfaces a backend failure as an exception rather than a silent Unknown.
/// </summary>
public sealed class GraphSensitivityLabelClientTests
{
    private const string ConfidentialId = "11111111-1111-1111-1111-111111111111";
    private const string HighlyConfidentialId = "22222222-2222-2222-2222-222222222222";

    private static readonly DateTimeOffset Now = new(2026, 6, 25, 12, 0, 0, TimeSpan.Zero);

    private static string CatalogJson() =>
        $$"""
        {
          "value": [
            { "id": "{{ConfidentialId}}", "name": "Confidential" },
            { "id": "{{HighlyConfidentialId}}", "name": "Highly Confidential" }
          ]
        }
        """;

    private static DataClassificationConfig Config(TimeSpan? catalogTtl = null) => new()
    {
        Mode = ClassificationEnforcementMode.Enforce,
        InformationProtection = new InformationProtectionProviderConfig
        {
            Enabled = true,
            GraphBaseUrl = "https://graph.test/v1.0",
            Scopes = ["https://graph.test/.default"],
            LabelCatalogCacheTtl = catalogTtl ?? TimeSpan.FromHours(1),
        },
    };

    private static GraphSensitivityLabelClient CreateClient(
        StubHandler handler, MutableTimeProvider time, TimeSpan? catalogTtl = null) =>
        new(new StubHttpClientFactory(handler), new FakeCredential(), Config(catalogTtl).InformationProtection, time,
            NullLogger<GraphSensitivityLabelClient>.Instance);

    [Fact]
    public async Task GetLabelAsync_NoEmbeddedLabelId_ReturnsUnknownWithoutCallingGraph()
    {
        var handler = new StubHandler(_ => Ok(CatalogJson()));
        var sut = CreateClient(handler, new MutableTimeProvider(Now));

        var result = await sut.GetLabelAsync(
            new AssetReference(AssetType.LocalFile, @"C:\notes\plain.txt"), CancellationToken.None);

        result.Source.Should().Be(LabelSource.None);
        result.Label.Should().BeNull();
        result.HasClassification.Should().BeFalse();
        handler.CallCount.Should().Be(0, "a plain path carries no label id, so no Graph round trip is needed");
    }

    [Fact]
    public async Task GetLabelAsync_BareGuidMatchingCatalog_ResolvesLabel()
    {
        var handler = new StubHandler(_ => Ok(CatalogJson()));
        var sut = CreateClient(handler, new MutableTimeProvider(Now));

        var result = await sut.GetLabelAsync(
            new AssetReference(AssetType.LocalFile, ConfidentialId), CancellationToken.None);

        result.Source.Should().Be(LabelSource.InformationProtection);
        result.Label!.Name.Should().Be("Confidential");
        result.Label.Id.Should().Be(ConfidentialId);
        result.IsStale.Should().BeFalse("Information Protection labels are embedded, not scan-derived");
    }

    [Fact]
    public async Task GetLabelAsync_MsipMarkerMatchingCatalog_ResolvesLabel()
    {
        var handler = new StubHandler(_ => Ok(CatalogJson()));
        var sut = CreateClient(handler, new MutableTimeProvider(Now));

        // The marker form MIP writes into a file's custom properties.
        var identifier = $"MSIP_Label_{HighlyConfidentialId}_Enabled=True";
        var result = await sut.GetLabelAsync(
            new AssetReference(AssetType.LocalFile, identifier), CancellationToken.None);

        result.Source.Should().Be(LabelSource.InformationProtection);
        result.Label!.Name.Should().Be("Highly Confidential");
    }

    [Fact]
    public async Task GetLabelAsync_LabelIdNotInCatalog_ReturnsUnknown()
    {
        var handler = new StubHandler(_ => Ok(CatalogJson()));
        var sut = CreateClient(handler, new MutableTimeProvider(Now));

        var result = await sut.GetLabelAsync(
            new AssetReference(AssetType.LocalFile, "99999999-9999-9999-9999-999999999999"),
            CancellationToken.None);

        result.Source.Should().Be(LabelSource.None);
        result.Label.Should().BeNull();
    }

    [Fact]
    public async Task GetLabelAsync_GraphReturnsError_ThrowsWithoutLeakingToken()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var sut = CreateClient(handler, new MutableTimeProvider(Now));

        var act = async () => await sut.GetLabelAsync(
            new AssetReference(AssetType.LocalFile, ConfidentialId), CancellationToken.None);

        var ex = await act.Should().ThrowAsync<InvalidOperationException>();
        ex.Which.Message.Should().Contain("500");
        ex.Which.Message.Should().NotContain(FakeCredential.TokenValue);
    }

    [Fact]
    public async Task GetLabelAsync_GraphReturns200WithoutValueCollection_Throws()
    {
        // A 200 whose body lacks the 'value' array is an unrecognized shape — it must fail closed rather
        // than caching an empty taxonomy that silently degrades labelled assets to Unknown for the TTL.
        var handler = new StubHandler(_ => Ok("{}"));
        var sut = CreateClient(handler, new MutableTimeProvider(Now));

        var act = async () => await sut.GetLabelAsync(
            new AssetReference(AssetType.LocalFile, ConfidentialId), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task GetLabelAsync_CachesCatalog_AcrossCallsWithinTtl()
    {
        var handler = new StubHandler(_ => Ok(CatalogJson()));
        var sut = CreateClient(handler, new MutableTimeProvider(Now));

        await sut.GetLabelAsync(new AssetReference(AssetType.LocalFile, ConfidentialId), CancellationToken.None);
        await sut.GetLabelAsync(new AssetReference(AssetType.LocalFile, HighlyConfidentialId), CancellationToken.None);

        handler.CallCount.Should().Be(1, "the taxonomy is fetched once and reused within the TTL");
    }

    [Fact]
    public async Task GetLabelAsync_RefetchesCatalog_AfterTtlExpires()
    {
        var handler = new StubHandler(_ => Ok(CatalogJson()));
        var time = new MutableTimeProvider(Now);
        var sut = CreateClient(handler, time, catalogTtl: TimeSpan.FromMinutes(30));

        await sut.GetLabelAsync(new AssetReference(AssetType.LocalFile, ConfidentialId), CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(31));
        await sut.GetLabelAsync(new AssetReference(AssetType.LocalFile, ConfidentialId), CancellationToken.None);

        handler.CallCount.Should().Be(2, "an expired taxonomy must be refreshed from Graph");
    }

    [Fact]
    public async Task GetLabelAsync_AttachesBearerTokenAndRequestsConfiguredScopes()
    {
        var handler = new StubHandler(_ => Ok(CatalogJson()));
        var credential = new FakeCredential();
        var sut = new GraphSensitivityLabelClient(
            new StubHttpClientFactory(handler), credential, Config().InformationProtection, new MutableTimeProvider(Now),
            NullLogger<GraphSensitivityLabelClient>.Instance);

        await sut.GetLabelAsync(new AssetReference(AssetType.LocalFile, ConfidentialId), CancellationToken.None);

        handler.LastAuthScheme.Should().Be("Bearer");
        handler.LastAuthToken.Should().Be(FakeCredential.TokenValue);
        credential.LastScopes.Should().Equal("https://graph.test/.default");
        handler.LastRequestUri!.AbsoluteUri.Should()
            .Be("https://graph.test/v1.0/security/dataSecurityAndGovernance/sensitivityLabels");
    }

    [Fact]
    public async Task GetLabelAsync_NullAsset_Throws()
    {
        var sut = CreateClient(new StubHandler(_ => Ok(CatalogJson())), new MutableTimeProvider(Now));

        var act = async () => await sut.GetLabelAsync(null!, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    private static HttpResponseMessage Ok(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        // disposeHandler: false so the shared stub survives disposal of the per-fetch client.
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        public Uri? LastRequestUri { get; private set; }
        public string? LastAuthScheme { get; private set; }
        public string? LastAuthToken { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequestUri = request.RequestUri;
            LastAuthScheme = request.Headers.Authorization?.Scheme;
            LastAuthToken = request.Headers.Authorization?.Parameter;
            return Task.FromResult(responder(request));
        }
    }

    private sealed class FakeCredential : TokenCredential
    {
        public const string TokenValue = "fake-access-token";
        public string[] LastScopes { get; private set; } = [];

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            LastScopes = requestContext.Scopes;
            return new AccessToken(TokenValue, Now.AddHours(1));
        }

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => new(GetToken(requestContext, cancellationToken));
    }

    private sealed class MutableTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}
