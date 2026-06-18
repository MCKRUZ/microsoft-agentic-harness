using System.Net;
using Azure.Core;
using Domain.Common.Config.AI.MCP;
using FluentAssertions;
using Infrastructure.AI.Identity;
using Xunit;

namespace Infrastructure.AI.Tests.Identity;

/// <summary>
/// Tests for <see cref="EntraTokenAuthHandler"/> covering per-request token injection,
/// rotation (a fresh token acquired for every request), and construction guards.
/// </summary>
public sealed class EntraTokenAuthHandlerTests
{
    private static readonly string[] Scopes = ["api://test-resource/.default"];

    [Fact]
    public async Task SendAsync_AttachesBearerTokenFromCredential()
    {
        var credential = new CountingCredential();
        using var capturing = new CapturingHandler();
        using var sut = new EntraTokenAuthHandler(credential, Scopes) { InnerHandler = capturing };
        using var invoker = new HttpMessageInvoker(sut);

        await invoker.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "https://server.test/mcp"),
            CancellationToken.None);

        capturing.SeenSchemes.Should().ContainSingle().Which.Should().Be("Bearer");
        capturing.SeenTokens.Should().ContainSingle().Which.Should().Be("token-1");
    }

    [Fact]
    public async Task SendAsync_AcquiresFreshTokenPerRequest()
    {
        // Proves the handler mints per request rather than capturing a token once at
        // construction — the basis for auto-rotation, since the Azure credential refreshes
        // the cached token near expiry on these same per-request calls.
        var credential = new CountingCredential();
        using var capturing = new CapturingHandler();
        using var sut = new EntraTokenAuthHandler(credential, Scopes) { InnerHandler = capturing };
        using var invoker = new HttpMessageInvoker(sut);

        await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://server.test/a"), CancellationToken.None);
        await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://server.test/b"), CancellationToken.None);

        credential.CallCount.Should().Be(2);
        capturing.SeenTokens.Should().Equal("token-1", "token-2");
    }

    [Fact]
    public async Task SendAsync_PassesRequestedScopesToCredential()
    {
        var credential = new CountingCredential();
        using var capturing = new CapturingHandler();
        using var sut = new EntraTokenAuthHandler(credential, Scopes) { InnerHandler = capturing };
        using var invoker = new HttpMessageInvoker(sut);

        await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://server.test/mcp"), CancellationToken.None);

        credential.LastScopes.Should().Equal("api://test-resource/.default");
    }

    [Fact]
    public void Constructor_NullCredential_Throws()
    {
        var act = () => new EntraTokenAuthHandler(null!, Scopes);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_EmptyScopes_Throws()
    {
        var act = () => new EntraTokenAuthHandler(new CountingCredential(), []);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_ManagedIdentityConfig_WiresInnerHandler()
    {
        var auth = new McpServerAuthConfig
        {
            Type = McpServerAuthType.Entra,
            Scopes = ["api://test-resource/.default"]
        };
        using var inner = new CapturingHandler();

        using var handler = EntraTokenAuthHandler.Create(auth, inner);

        handler.InnerHandler.Should().BeSameAs(inner);
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<string?> SeenSchemes { get; } = [];
        public List<string?> SeenTokens { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            SeenSchemes.Add(request.Headers.Authorization?.Scheme);
            SeenTokens.Add(request.Headers.Authorization?.Parameter);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private sealed class CountingCredential : TokenCredential
    {
        public int CallCount { get; private set; }
        public string[] LastScopes { get; private set; } = [];

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            LastScopes = requestContext.Scopes;
            return new AccessToken($"token-{++CallCount}", DateTimeOffset.MaxValue);
        }

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => new(GetToken(requestContext, cancellationToken));
    }
}
