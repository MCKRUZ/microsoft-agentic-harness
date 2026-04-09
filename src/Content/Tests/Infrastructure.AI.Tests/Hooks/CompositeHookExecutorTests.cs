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

public class CompositeHookExecutorTests
{
    private readonly InMemoryHookRegistry _registry;
    private readonly CompositeHookExecutor _sut;
    private readonly Mock<IHttpClientFactory> _httpClientFactory;

    public CompositeHookExecutorTests()
    {
        _registry = new InMemoryHookRegistry(
            new GlobPatternMatcher(),
            Mock.Of<ILogger<InMemoryHookRegistry>>());

        _httpClientFactory = new Mock<IHttpClientFactory>();

        var config = new HooksConfig { Enabled = true, MaxParallelHooks = 10 };
        var optionsMonitor = Mock.Of<IOptionsMonitor<HooksConfig>>(
            m => m.CurrentValue == config);

        _sut = new CompositeHookExecutor(
            _registry,
            _httpClientFactory.Object,
            optionsMonitor,
            Mock.Of<ILogger<CompositeHookExecutor>>());
    }

    [Fact]
    public async Task ExecuteHooks_NoMatchingHooks_ReturnsEmpty()
    {
        var context = CreateContext(HookEvent.PreToolUse);

        var results = await _sut.ExecuteHooksAsync(HookEvent.PreToolUse, context);

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteHooks_ParallelExecution_AllComplete()
    {
        _registry.Register(CreatePromptHook("h1", "Template for {ToolName}"));
        _registry.Register(CreatePromptHook("h2", "Another for {AgentId}"));

        var context = CreateContext(HookEvent.PreToolUse, toolName: "file_read", agentId: "agent-1");

        var results = await _sut.ExecuteHooksAsync(HookEvent.PreToolUse, context);

        results.Should().HaveCount(2);
        results.Should().OnlyContain(r => r.Continue);
    }

    [Fact]
    public async Task ExecuteHooks_OneHookThrows_OthersStillExecute()
    {
        // Register a prompt hook that will succeed
        _registry.Register(CreatePromptHook("good", "Template {ToolName}"));

        // Register an HTTP hook with no URL (will log warning and return PassThrough, not throw)
        // To truly test error isolation, we use an Http hook with a valid URL but no client setup
        _registry.Register(new HookDefinition
        {
            Id = "bad",
            Event = HookEvent.PreToolUse,
            Type = HookType.Http,
            WebhookUrl = "http://localhost:9999/nonexistent",
            Priority = 50
        });

        // The HTTP client factory returns a client that will fail to connect
        _httpClientFactory.Setup(f => f.CreateClient("HookWebhook"))
            .Returns(new HttpClient());

        var context = CreateContext(HookEvent.PreToolUse, toolName: "test_tool");

        var results = await _sut.ExecuteHooksAsync(HookEvent.PreToolUse, context);

        // Both hooks execute — the failing one returns PassThrough due to error isolation
        results.Should().HaveCount(2);
        results.Should().OnlyContain(r => r.Continue);
    }

    [Fact]
    public async Task ExecuteHooks_Timeout_HookCancelled()
    {
        // Create an HTTP hook with a very short timeout
        _registry.Register(new HookDefinition
        {
            Id = "slow",
            Event = HookEvent.PreToolUse,
            Type = HookType.Http,
            WebhookUrl = "http://10.255.255.1/timeout", // non-routable IP to trigger timeout
            TimeoutMs = 100,
            Priority = 100
        });

        _httpClientFactory.Setup(f => f.CreateClient("HookWebhook"))
            .Returns(new HttpClient());

        var context = CreateContext(HookEvent.PreToolUse, toolName: "test_tool");

        var results = await _sut.ExecuteHooksAsync(HookEvent.PreToolUse, context);

        // Hook timed out, returns PassThrough
        results.Should().ContainSingle().Which.Continue.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteHooks_RunOnce_UnregisteredAfterExecution()
    {
        _registry.Register(new HookDefinition
        {
            Id = "once",
            Event = HookEvent.SessionStart,
            Type = HookType.Prompt,
            PromptTemplate = "Session started for {AgentId}",
            RunOnce = true
        });

        var context = CreateContext(HookEvent.SessionStart, agentId: "agent-1");

        // First execution — hook fires
        var firstResults = await _sut.ExecuteHooksAsync(HookEvent.SessionStart, context);
        firstResults.Should().ContainSingle();

        // Second execution — hook should be unregistered
        var secondResults = await _sut.ExecuteHooksAsync(HookEvent.SessionStart, context);
        secondResults.Should().BeEmpty();
    }

    [Fact]
    public async Task PromptHook_ReplacesTemplateVariables()
    {
        _registry.Register(CreatePromptHook(
            "template",
            "Tool={ToolName} Agent={AgentId} Conv={ConversationId} Turn={TurnNumber} Event={Event}"));

        var context = new HookExecutionContext
        {
            Event = HookEvent.PreToolUse,
            ToolName = "file_read",
            AgentId = "agent-42",
            ConversationId = "conv-99",
            TurnNumber = 7
        };

        var results = await _sut.ExecuteHooksAsync(HookEvent.PreToolUse, context);

        var result = results.Should().ContainSingle().Subject;
        result.AdditionalContext.Should().Be(
            "Tool=file_read Agent=agent-42 Conv=conv-99 Turn=7 Event=PreToolUse");
    }

    private static HookExecutionContext CreateContext(
        HookEvent hookEvent,
        string? toolName = null,
        string? agentId = null)
    {
        return new HookExecutionContext
        {
            Event = hookEvent,
            ToolName = toolName,
            AgentId = agentId
        };
    }

    private static HookDefinition CreatePromptHook(string id, string template, int priority = 100)
    {
        return new HookDefinition
        {
            Id = id,
            Event = HookEvent.PreToolUse,
            Type = HookType.Prompt,
            PromptTemplate = template,
            Priority = priority
        };
    }
}
