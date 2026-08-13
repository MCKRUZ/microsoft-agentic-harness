using Application.AI.Common.Exceptions;
using Domain.Common.Config;
using System.Collections.Concurrent;
using Domain.Common.Config.AI.MCP;
using Infrastructure.AI.Egress;
using Infrastructure.AI.MCP.Services;
using Infrastructure.AI.Tests.Egress.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Infrastructure.AI.Tests.Egress;

/// <summary>
/// End-to-end proof that outbound MCP server connections are SSRF-guarded by
/// construction. <see cref="McpConnectionManager"/> builds its HTTP client on the
/// <see cref="AntiSsrfHandlerFactory"/> handler, so a server URL that resolves to an
/// internal, loopback, or cloud-metadata address is refused at connect time and
/// surfaced as <see cref="McpConnectionException"/> — closing the gap where MCP
/// connections previously bypassed SSRF defenses entirely.
/// </summary>
public sealed class McpSsrfProtectionTests
{
    [Theory]
    [InlineData("http://169.254.169.254/mcp")]   // cloud metadata (IMDS)
    [InlineData("http://10.0.0.1/mcp")]           // RFC 1918 private
    [InlineData("http://127.0.0.1:9/mcp")]        // loopback
    public async Task GetClientAsync_HttpServerTargetingInternalAddress_IsBlocked(string url)
    {
        var sut = BuildManager(url);

        await Assert.ThrowsAsync<McpConnectionException>(
            () => sut.GetClientAsync("internal"));
    }

    private static McpConnectionManager BuildManager(string url)
    {
        // AllowPlainTextHttp = true so the deny verdict provably comes from the
        // IP-range filter, not the plain-text-HTTP rule.
        var cfg = new AppConfig();
        cfg.AI.Egress.AllowPlainTextHttp = true;

        var antiSsrf = new AntiSsrfHandlerFactory(new TestConfig.StaticOptionsMonitor<AppConfig>(cfg));

        var config = new McpServersConfig
        {
            Servers = new ConcurrentDictionary<string, McpServerDefinition>
            {
                ["internal"] = new()
                {
                    Enabled = true,
                    Type = McpServerType.Http,
                    Url = url,
                    StartupTimeoutSeconds = 3
                }
            }
        };

        return new McpConnectionManager(
            NullLogger<McpConnectionManager>.Instance,
            NullLoggerFactory.Instance,
            antiSsrf,
            config,
            new BundleOwnedMcpServerRegistry());
    }
}
