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
/// Additional integration tests for <see cref="CompositeHookExecutor"/> covering
/// uncovered code paths: disabled hooks, SSRF validation, command/middleware stubs,
/// prompt hooks with missing templates, and concurrency throttling.
/// </summary>
public class CompositeHookExecutorIntegrationTests
{
    private readonly InMemoryHookRegistry _registry;
    private readonly Mock<IHttpClientFactory> _httpClientFactory;

    public CompositeHookExecutorIntegrationTests()
    {
        _registry = new InMemoryHookRegistry(
            new GlobPatternMatcher(),
            Mock.Of<ILogger<InMemoryHookRegistry>>());

        _httpClientFactory = new Mock<IHttpClientFactory>();
    }

    private CompositeHookExecutor CreateSut(bool enabled = true, int maxParallel = 10)
    {
        var config = new HooksConfig { Enabled = enabled, MaxParallelHooks = maxParallel };
        var optionsMonitor = Mock.Of<IOptionsMonitor<HooksConfig>>(
            m => m.CurrentValue == config);

        return new CompositeHookExecutor(
            _registry,
            _httpClientFactory.Object,
            optionsMonitor,
            Mock.Of<ILogger<CompositeHookExecutor>>());
    }

    private static HookExecutionContext CreateContext(
        HookEvent hookEvent,
        string? toolName = null,
        string? agentId = null,
        string? conversationId = null,
        int? turnNumber = null)
    {
        return new HookExecutionContext
        {
            Event = hookEvent,
            ToolName = toolName,
            AgentId = agentId,
            ConversationId = conversationId,
            TurnNumber = turnNumber
        };
    }

    // ── Hooks disabled ───────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteHooks_Disabled_ReturnsEmpty()
    {
        _registry.Register(new HookDefinition
        {
            Id = "should-not-run",
            Event = HookEvent.PreToolUse,
            Type = HookType.Prompt,
            PromptTemplate = "test"
        });

        var sut = CreateSut(enabled: false);
        var context = CreateContext(HookEvent.PreToolUse);

        var results = await sut.ExecuteHooksAsync(HookEvent.PreToolUse, context);

        results.Should().BeEmpty();
    }

    // ── Command hook (deferred) ──────────────────────────────────────────────

    [Fact]
    public async Task ExecuteHooks_CommandHook_ReturnsPassThrough()
    {
        _registry.Register(new HookDefinition
        {
            Id = "cmd-hook",
            Event = HookEvent.PreToolUse,
            Type = HookType.Command,
            CommandLine = "echo test"
        });

        var sut = CreateSut();
        var context = CreateContext(HookEvent.PreToolUse);

        var results = await sut.ExecuteHooksAsync(HookEvent.PreToolUse, context);

        results.Should().ContainSingle()
            .Which.Continue.Should().BeTrue();
    }

    // ── Middleware hook (not implemented) ─────────────────────────────────────

    [Fact]
    public async Task ExecuteHooks_MiddlewareHook_ReturnsPassThrough()
    {
        _registry.Register(new HookDefinition
        {
            Id = "mw-hook",
            Event = HookEvent.SessionStart,
            Type = HookType.Middleware,
            MiddlewareTypeName = "SomeMiddleware"
        });

        var sut = CreateSut();
        var context = CreateContext(HookEvent.SessionStart);

        var results = await sut.ExecuteHooksAsync(HookEvent.SessionStart, context);

        results.Should().ContainSingle()
            .Which.Continue.Should().BeTrue();
    }

    // ── Prompt hook: missing template ────────────────────────────────────────

    [Fact]
    public async Task ExecuteHooks_PromptHookNoTemplate_ReturnsPassThrough()
    {
        _registry.Register(new HookDefinition
        {
            Id = "no-template",
            Event = HookEvent.PreToolUse,
            Type = HookType.Prompt,
            PromptTemplate = null
        });

        var sut = CreateSut();
        var context = CreateContext(HookEvent.PreToolUse);

        var results = await sut.ExecuteHooksAsync(HookEvent.PreToolUse, context);

        var result = results.Should().ContainSingle().Subject;
        result.Continue.Should().BeTrue();
        result.AdditionalContext.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteHooks_PromptHookEmptyTemplate_ReturnsPassThrough()
    {
        _registry.Register(new HookDefinition
        {
            Id = "empty-template",
            Event = HookEvent.PreToolUse,
            Type = HookType.Prompt,
            PromptTemplate = "   "
        });

        var sut = CreateSut();
        var context = CreateContext(HookEvent.PreToolUse);

        var results = await sut.ExecuteHooksAsync(HookEvent.PreToolUse, context);

        results.Should().ContainSingle()
            .Which.AdditionalContext.Should().BeNull();
    }

    // ── Prompt hook: all template variables ──────────────────────────────────

    [Fact]
    public async Task ExecuteHooks_PromptHookWithNullContext_ReplacesWithEmpty()
    {
        _registry.Register(new HookDefinition
        {
            Id = "null-vars",
            Event = HookEvent.PreToolUse,
            Type = HookType.Prompt,
            PromptTemplate = "Tool={ToolName} Agent={AgentId} Conv={ConversationId} Turn={TurnNumber}"
        });

        var sut = CreateSut();
        var context = CreateContext(HookEvent.PreToolUse);

        var results = await sut.ExecuteHooksAsync(HookEvent.PreToolUse, context);

        var result = results.Should().ContainSingle().Subject;
        result.AdditionalContext.Should().Be("Tool= Agent= Conv= Turn=");
    }

    // ── HTTP hook: missing URL ───────────────────────────────────────────────

    [Fact]
    public async Task ExecuteHooks_HttpHookNoUrl_ReturnsPassThrough()
    {
        _registry.Register(new HookDefinition
        {
            Id = "no-url",
            Event = HookEvent.PreToolUse,
            Type = HookType.Http,
            WebhookUrl = null
        });

        var sut = CreateSut();
        var context = CreateContext(HookEvent.PreToolUse);

        var results = await sut.ExecuteHooksAsync(HookEvent.PreToolUse, context);

        results.Should().ContainSingle()
            .Which.Continue.Should().BeTrue();
    }

    // ── HTTP hook: SSRF blocked URLs ─────────────────────────────────────────

    [Theory]
    [InlineData("http://localhost/webhook")]
    [InlineData("http://127.0.0.1/webhook")]
    [InlineData("http://10.0.0.1/webhook")]
    [InlineData("http://169.254.169.254/latest/meta-data")]
    [InlineData("http://172.16.0.1/webhook")]
    [InlineData("http://192.168.1.1/webhook")]
    [InlineData("http://metadata.google.internal/computeMetadata")]
    public async Task ExecuteHooks_HttpHookSsrfUrl_ReturnsPassThrough(string url)
    {
        _registry.Register(new HookDefinition
        {
            Id = "ssrf",
            Event = HookEvent.PreToolUse,
            Type = HookType.Http,
            WebhookUrl = url
        });

        var sut = CreateSut();
        var context = CreateContext(HookEvent.PreToolUse);

        var results = await sut.ExecuteHooksAsync(HookEvent.PreToolUse, context);

        results.Should().ContainSingle()
            .Which.Continue.Should().BeTrue("SSRF URL should be blocked");
    }

    [Theory]
    [InlineData("ftp://example.com/webhook")]
    [InlineData("file:///etc/passwd")]
    public async Task ExecuteHooks_HttpHookNonHttpScheme_ReturnsPassThrough(string url)
    {
        _registry.Register(new HookDefinition
        {
            Id = "bad-scheme",
            Event = HookEvent.PreToolUse,
            Type = HookType.Http,
            WebhookUrl = url
        });

        var sut = CreateSut();
        var context = CreateContext(HookEvent.PreToolUse);

        var results = await sut.ExecuteHooksAsync(HookEvent.PreToolUse, context);

        results.Should().ContainSingle()
            .Which.Continue.Should().BeTrue("non-HTTP scheme should be blocked");
    }

    // ── Concurrency throttling ───────────────────────────────────────────────

    [Fact]
    public async Task ExecuteHooks_MaxParallelOne_ExecutesSequentially()
    {
        for (int i = 0; i < 3; i++)
        {
            _registry.Register(new HookDefinition
            {
                Id = $"seq-{i}",
                Event = HookEvent.PreToolUse,
                Type = HookType.Prompt,
                PromptTemplate = $"Hook {i}"
            });
        }

        var sut = CreateSut(maxParallel: 1);
        var context = CreateContext(HookEvent.PreToolUse);

        var results = await sut.ExecuteHooksAsync(HookEvent.PreToolUse, context);

        results.Should().HaveCount(3);
        results.Should().OnlyContain(r => r.Continue);
    }

    // ── Multiple RunOnce hooks ───────────────────────────────────────────────

    [Fact]
    public async Task ExecuteHooks_MultipleRunOnce_AllUnregistered()
    {
        _registry.Register(new HookDefinition
        {
            Id = "once-a",
            Event = HookEvent.SessionStart,
            Type = HookType.Prompt,
            PromptTemplate = "A",
            RunOnce = true
        });
        _registry.Register(new HookDefinition
        {
            Id = "once-b",
            Event = HookEvent.SessionStart,
            Type = HookType.Prompt,
            PromptTemplate = "B",
            RunOnce = true
        });

        var sut = CreateSut();
        var context = CreateContext(HookEvent.SessionStart);

        var first = await sut.ExecuteHooksAsync(HookEvent.SessionStart, context);
        first.Should().HaveCount(2);

        var second = await sut.ExecuteHooksAsync(HookEvent.SessionStart, context);
        second.Should().BeEmpty();
    }
}
