using System.Collections.Concurrent;
using Domain.Common.Config.AI.MCP;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Infrastructure.AI.Tests.Plugins;

/// <summary>
/// Proves <see cref="McpServersConfig.Servers"/> binds correctly from real <c>IConfiguration</c>, the
/// same way appsettings.json-declared MCP servers are bound in production
/// (<c>services.Configure&lt;AIConfig&gt;(configuration.GetSection("AppConfig:AI"))</c> — see
/// <c>Presentation.ConsoleUI/appsettings.json</c>'s <c>AI:McpServers:Servers:&lt;name&gt;</c> shape,
/// which is the exact nesting this test's JSON mirrors).
/// </summary>
/// <remarks>
/// A first version of this test used the wrong JSON shape (bound a section directly onto
/// <see cref="McpServersConfig"/> without the intermediate <c>Servers</c> key) and appeared to prove
/// that a <see cref="ConcurrentDictionary{TKey,TValue}"/>-typed property could not be populated by the
/// standard binder — the test itself was wrong, not the binder. A corrected control proved
/// <see cref="ConcurrentDictionary{TKey,TValue}"/> binds identically to a plain
/// <see cref="Dictionary{TKey,TValue}"/>. Kept as a standing regression guard, and as a record of that
/// correction, so nobody re-derives the same wrong conclusion from a bad control in the future.
/// </remarks>
public sealed class McpServersConfigBindingTests
{
    [Fact]
    public void Bind_JsonConfiguredServers_PopulatesServersDictionary()
    {
        var json = """
            {
              "McpServers": {
                "Servers": {
                  "filesystem": {
                    "Type": "Stdio",
                    "Command": "npx",
                    "Args": ["-y", "server-name"]
                  },
                  "remote": {
                    "Type": "Http",
                    "Url": "https://tools.example.com/mcp"
                  }
                }
              }
            }
            """;

        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        var configuration = new ConfigurationBuilder().AddJsonStream(stream).Build();

        var target = new McpServersConfig();
        configuration.GetSection("McpServers").Bind(target);

        target.Servers.Should().HaveCount(2);
        target.Servers.Should().ContainKey("filesystem");
        target.Servers["filesystem"].Command.Should().Be("npx");
        target.Servers["filesystem"].Args.Should().BeEquivalentTo(["-y", "server-name"]);
        target.Servers["remote"].Type.Should().Be(McpServerType.Http);
        target.Servers["remote"].Url.Should().Be("https://tools.example.com/mcp");
    }
}
