using Application.AI.Common.Services.Agent;
using FluentAssertions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.Services.Agent;

public sealed class ToolPermissionFilterTests
{
    /// <summary>
    /// Exposes the protected <c>ProvideAIContextAsync</c> for unit testing.
    /// <see cref="ToolPermissionFilter"/> is not sealed so this is safe.
    /// </summary>
    private sealed class TestableFilter(IEnumerable<string> allowedTools) : ToolPermissionFilter(allowedTools)
    {
        public ValueTask<AIContext> InvokeAsync(AIContextProvider.InvokingContext context, CancellationToken ct = default)
            => ProvideAIContextAsync(context, ct);
    }

    private static AITool MakeTool(string name)
    {
        var mock = new Mock<AITool>();
        mock.Setup(t => t.Name).Returns(name);
        return mock.Object;
    }

    // ToolPermissionFilter only accesses context.AIContext — Agent and Session are unused by the SUT.
    private static AIContextProvider.InvokingContext MakeContext(AIContext aiContext) =>
        new(new Mock<AIAgent>().Object, new Mock<AgentSession>().Object, aiContext);

    // ── empty allow-list ─────────────────────────────────────────────────────

    [Fact]
    public async Task EmptyAllowList_NoTools_ReturnsContextUnchanged()
    {
        var filter = new TestableFilter([]);
        var aiContext = new AIContext();
        var context = MakeContext(aiContext);

        var result = await filter.InvokeAsync(context);

        result.Should().BeSameAs(aiContext);
    }

    [Fact]
    public async Task EmptyAllowList_WithTools_AllToolsPassThrough()
    {
        var filter = new TestableFilter([]);
        var aiContext = new AIContext { Tools = [MakeTool("Read"), MakeTool("Write")] };
        var context = MakeContext(aiContext);

        var result = await filter.InvokeAsync(context);

        result.Tools.Should().HaveCount(2);
    }

    // ── filtering behavior ───────────────────────────────────────────────────

    [Fact]
    public async Task AllowedTool_IsRetained()
    {
        var filter = new TestableFilter(["Read"]);
        var aiContext = new AIContext { Tools = [MakeTool("Read")] };
        var context = MakeContext(aiContext);

        var result = await filter.InvokeAsync(context);

        result.Tools.Should().HaveCount(1);
        result.Tools!.First().Name.Should().Be("Read");
    }

    [Fact]
    public async Task DisallowedTool_IsStripped()
    {
        var filter = new TestableFilter(["Read"]);
        var aiContext = new AIContext { Tools = [MakeTool("Write")] };
        var context = MakeContext(aiContext);

        var result = await filter.InvokeAsync(context);

        result.Tools.Should().BeEmpty();
    }

    [Fact]
    public async Task MixedTools_OnlyAllowedToolsRetained()
    {
        var filter = new TestableFilter(["Read", "Search"]);
        var aiContext = new AIContext
        {
            Tools = [MakeTool("Read"), MakeTool("Write"), MakeTool("Search"), MakeTool("Delete")]
        };
        var context = MakeContext(aiContext);

        var result = await filter.InvokeAsync(context);

        result.Tools.Should().HaveCount(2);
        result.Tools!.Select(t => t.Name).Should().BeEquivalentTo(["Read", "Search"]);
    }

    [Fact]
    public async Task AllToolsAllowed_ReturnsSameContextInstance()
    {
        var filter = new TestableFilter(["Read", "Write"]);
        var aiContext = new AIContext { Tools = [MakeTool("Read"), MakeTool("Write")] };
        var context = MakeContext(aiContext);

        var result = await filter.InvokeAsync(context);

        result.Should().BeSameAs(aiContext, "no tools were removed so a new AIContext allocation is unnecessary");
    }

    // ── case insensitivity ───────────────────────────────────────────────────

    [Fact]
    public async Task ToolNameMatching_IsCaseInsensitive()
    {
        var filter = new TestableFilter(["read"]);
        var aiContext = new AIContext { Tools = [MakeTool("READ")] };
        var context = MakeContext(aiContext);

        var result = await filter.InvokeAsync(context);

        result.Tools.Should().HaveCount(1);
    }

    // ── null / empty tools in context ────────────────────────────────────────

    [Fact]
    public async Task NullToolsInContext_ReturnsContextUnchanged()
    {
        var filter = new TestableFilter(["Read"]);
        var aiContext = new AIContext { Tools = null };
        var context = MakeContext(aiContext);

        var result = await filter.InvokeAsync(context);

        result.Should().BeSameAs(aiContext);
    }

    [Fact]
    public async Task EmptyToolsInContext_ReturnsContextUnchanged()
    {
        var filter = new TestableFilter(["Read"]);
        var aiContext = new AIContext { Tools = [] };
        var context = MakeContext(aiContext);

        var result = await filter.InvokeAsync(context);

        result.Should().BeSameAs(aiContext);
    }

    // ── context field preservation ───────────────────────────────────────────

    [Fact]
    public async Task FilteredContext_PreservesInstructions()
    {
        var filter = new TestableFilter(["Read"]);
        var aiContext = new AIContext
        {
            Instructions = "You are a helpful assistant.",
            Tools = [MakeTool("Write")] // will be filtered out
        };
        var context = MakeContext(aiContext);

        var result = await filter.InvokeAsync(context);

        result.Instructions.Should().Be("You are a helpful assistant.");
    }

    [Fact]
    public async Task FilteredContext_PreservesMessages()
    {
        var filter = new TestableFilter(["Read"]);
        var messages = new List<ChatMessage> { new(ChatRole.User, "Hello") };
        var aiContext = new AIContext
        {
            Messages = messages,
            Tools = [MakeTool("Write")] // will be filtered out
        };
        var context = MakeContext(aiContext);

        var result = await filter.InvokeAsync(context);

        result.Messages.Should().BeEquivalentTo(messages);
    }
}
