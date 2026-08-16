using System.Collections.Concurrent;
using Application.AI.Common.Exceptions;
using Application.AI.Common.Interfaces.Sandbox;
using Domain.AI.Sandbox;
using Domain.Common;
using Domain.Common.Config.AI.MCP;
using FluentAssertions;
using Infrastructure.AI.Bundles;
using Infrastructure.AI.MCP.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Infrastructure.AI.MCP.Tests.Services;

/// <summary>
/// Coverage for the #371 routing decision in <c>McpConnectionManager.CreateTransport</c>'s stdio
/// arm: a bundle-owned stdio server must go through a sandboxed session, while a host-configured
/// (trusted, plugin-installed) stdio server must keep launching directly, completely untouched by
/// the sandbox. Uses a fake keyed <see cref="ISandboxSessionFactory"/> registered into an isolated
/// root provider (via <c>McpConnectionManagerBundleEgressSupport</c>'s override) so the routing
/// itself is provable without a real sandbox or Docker daemon.
/// </summary>
public sealed class McpConnectionManagerSandboxedStdioTests
{
    private readonly FakeSandboxSessionFactory _fakeSessionFactory = new();

    private McpConnectionManager CreateManager(McpServersConfig hostConfig, BundleOwnedMcpServerRegistry bundleOwned)
    {
        var rootServices = McpConnectionManagerBundleEgressSupport.BuildRootServices(services =>
        {
            services.AddKeyedSingleton<ISandboxSessionFactory>(SandboxIsolationLevel.Container, _fakeSessionFactory);
        });

        return McpConnectionManagerBundleEgressSupport.CreateManager(
            Mock.Of<ILogger<McpConnectionManager>>(),
            new Mock<ILoggerFactory>().Object,
            TestSsrf.HandlerFactory(),
            hostConfig,
            bundleOwned,
            rootServices);
    }

    [Fact]
    public async Task GetClientAsync_BundleOwnedStdioServer_RoutesThroughTheSandboxSessionFactory()
    {
        var bundleOwned = new BundleOwnedMcpServerRegistry();
        bundleOwned.TryAdd("b1:local-tool", new McpServerDefinition
        {
            Enabled = true,
            Type = McpServerType.Stdio,
            Command = "attacker-chosen-binary",
            StartupTimeoutSeconds = 1
        });
        var sut = CreateManager(new McpServersConfig(), bundleOwned);

        var act = () => sut.GetClientAsync("b1:local-tool");

        var exception = await act.Should().ThrowAsync<McpConnectionException>();
        _fakeSessionFactory.WasCalled.Should().BeTrue(
            "a bundle-owned stdio server must start a sandboxed session, not launch the process directly");
        exception.Which.Message.Should().Contain("failed to start",
            "the failure must come from the sandbox session factory, not a host-process spawn attempt");
        exception.Which.Message.Should().NotContain("allowed programs list",
            "that message belongs to the unsandboxed process launch path this server must never reach");
    }

    [Fact]
    public async Task GetClientAsync_BundleOwnedStdioServer_PassesCommandAndArgsThroughToTheSessionRequest()
    {
        var bundleOwned = new BundleOwnedMcpServerRegistry();
        bundleOwned.TryAdd("b1:local-tool", new McpServerDefinition
        {
            Enabled = true,
            Type = McpServerType.Stdio,
            Command = "npx",
            Args = ["-y", "echo-mcp"],
            StartupTimeoutSeconds = 1
        });
        var sut = CreateManager(new McpServersConfig(), bundleOwned);

        await Assert.ThrowsAsync<McpConnectionException>(() => sut.GetClientAsync("b1:local-tool"));

        _fakeSessionFactory.LastRequest.Should().NotBeNull();
        _fakeSessionFactory.LastRequest!.Command.Should().Be("npx");
        _fakeSessionFactory.LastRequest.ArgumentList.Should().BeEquivalentTo(["-y", "echo-mcp"]);
        _fakeSessionFactory.LastRequest.PermissionProfile.MinimumIsolation.Should().Be(SandboxIsolationLevel.Container);
    }

    [Fact]
    public async Task GetClientAsync_HostConfiguredStdioServer_NeverReachesTheSandbox()
    {
        // The trust-tier boundary #371 must not blur: a host-installed plugin's stdio server keeps
        // launching directly on the host, exactly as before this feature existed.
        var hostConfig = new McpServersConfig
        {
            Servers = new ConcurrentDictionary<string, McpServerDefinition>
            {
                ["plugin:local-tool"] = new()
                {
                    Enabled = true,
                    Type = McpServerType.Stdio,
                    Command = "nonexistent-binary",
                    StartupTimeoutSeconds = 1
                }
            }
        };
        var sut = CreateManager(hostConfig, new BundleOwnedMcpServerRegistry());

        var act = () => sut.GetClientAsync("plugin:local-tool");

        await act.Should().ThrowAsync<McpConnectionException>();
        _fakeSessionFactory.WasCalled.Should().BeFalse(
            "a host-configured server is a different trust tier and must never route through the sandbox");
    }

    private sealed class FakeSandboxSessionFactory : ISandboxSessionFactory
    {
        public bool WasCalled { get; private set; }
        public SandboxSessionRequest? LastRequest { get; private set; }

        public Task<Result<ISandboxSession>> StartSessionAsync(SandboxSessionRequest request, CancellationToken ct)
        {
            WasCalled = true;
            LastRequest = request;
            return Task.FromResult(Result<ISandboxSession>.Fail("fake factory: not starting a real session"));
        }
    }
}
