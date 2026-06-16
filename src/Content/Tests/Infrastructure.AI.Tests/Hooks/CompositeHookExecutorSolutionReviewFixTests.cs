using Domain.AI.Hooks;
using Domain.Common.Config.AI.Hooks;
using FluentAssertions;
using Infrastructure.AI.Hooks;
using Infrastructure.AI.Permissions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Hooks;

/// <summary>
/// Regression tests for the solution-review finding that the webhook SSRF guard (H-2)
/// was bypassable via IPv4-mapped IPv6 and IPv6 link-local/unique-local literals
/// (only IPv4 dotted-quad literals were blocked). A blocked URL must short-circuit
/// before any outbound HTTP request, which we detect by failing if the
/// <see cref="IHttpClientFactory"/> is ever invoked.
/// </summary>
public class CompositeHookExecutorSolutionReviewFixTests
{
    private readonly InMemoryHookRegistry _registry;
    private readonly Mock<IHttpClientFactory> _httpClientFactory;
    private readonly CompositeHookExecutor _sut;

    public CompositeHookExecutorSolutionReviewFixTests()
    {
        _registry = new InMemoryHookRegistry(
            new GlobPatternMatcher(),
            Mock.Of<ILogger<InMemoryHookRegistry>>());

        // If the guard does its job the factory is never touched. Throw if it is,
        // so a regression (URL allowed through) surfaces as a failure rather than
        // a silent outbound connection attempt.
        _httpClientFactory = new Mock<IHttpClientFactory>(MockBehavior.Strict);

        var config = new HooksConfig { Enabled = true, MaxParallelHooks = 10 };
        var optionsMonitor = Mock.Of<IOptionsMonitor<HooksConfig>>(m => m.CurrentValue == config);

        _sut = new CompositeHookExecutor(
            _registry,
            _httpClientFactory.Object,
            optionsMonitor,
            Mock.Of<ILogger<CompositeHookExecutor>>());
    }

    [Theory]
    // IPv4-mapped IPv6 literal for the cloud-metadata address (was allowed: 16-byte branch skipped).
    [InlineData("http://[::ffff:169.254.169.254]/latest/meta-data/")]
    // IPv4-mapped IPv6 literal for an RFC 1918 host (was allowed).
    [InlineData("http://[::ffff:10.0.0.5]/internal")]
    // IPv6 loopback literal (was allowed: only IPv4 127.x was caught).
    [InlineData("http://[::1]/admin")]
    // IPv6 link-local fe80::/10 (was never checked).
    [InlineData("http://[fe80::1]/internal")]
    // IPv6 unique-local fc00::/7 (was never checked).
    [InlineData("http://[fc00::1]/internal")]
    [InlineData("http://[fd12:3456::1]/internal")]
    public async Task ExecuteHooks_HttpHookTargetsReservedIpv6Literal_BlocksWithoutOutboundRequest(string url)
    {
        _registry.Register(new HookDefinition
        {
            Id = "ssrf",
            Event = HookEvent.PreToolUse,
            Type = HookType.Http,
            WebhookUrl = url,
            Priority = 100
        });

        var context = new HookExecutionContext
        {
            Event = HookEvent.PreToolUse,
            ToolName = "test_tool"
        };

        var results = await _sut.ExecuteHooksAsync(HookEvent.PreToolUse, context);

        // Blocked hook returns a pass-through result and never constructs an HTTP client.
        results.Should().ContainSingle().Which.Continue.Should().BeTrue();
        _httpClientFactory.Verify(f => f.CreateClient(It.IsAny<string>()), Times.Never);
    }

    [Theory]
    // Plain IPv4 reserved literals — must remain blocked (no regression of the original guard).
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://10.0.0.1/internal")]
    [InlineData("http://172.16.5.4/internal")]
    [InlineData("http://192.168.1.1/internal")]
    public async Task ExecuteHooks_HttpHookTargetsReservedIpv4Literal_BlocksWithoutOutboundRequest(string url)
    {
        _registry.Register(new HookDefinition
        {
            Id = "ssrf",
            Event = HookEvent.PreToolUse,
            Type = HookType.Http,
            WebhookUrl = url,
            Priority = 100
        });

        var context = new HookExecutionContext
        {
            Event = HookEvent.PreToolUse,
            ToolName = "test_tool"
        };

        var results = await _sut.ExecuteHooksAsync(HookEvent.PreToolUse, context);

        results.Should().ContainSingle().Which.Continue.Should().BeTrue();
        _httpClientFactory.Verify(f => f.CreateClient(It.IsAny<string>()), Times.Never);
    }
}
