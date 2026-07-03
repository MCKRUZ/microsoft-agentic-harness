using Application.AI.Common.Interfaces.Tools;
using FluentAssertions;
using Infrastructure.AI.Tools;
using Xunit;

namespace Infrastructure.AI.Tests.Tools;

/// <summary>
/// Tests for <see cref="RenderTableTool"/> — the generative-UI tool that displays a data table via the
/// client round-trip. Covers operation validation, the no-client failure, structure validation (missing/
/// empty columns, non-array rows, a non-array row), the serialized payload, and the ack.
/// </summary>
public sealed class RenderTableToolTests
{
    private static Dictionary<string, object?> Args(params (string Key, object? Value)[] pairs)
    {
        var d = new Dictionary<string, object?>();
        foreach (var (k, v) in pairs) d[k] = v;
        return d;
    }

    private static object[] Columns(params string[] columns) => columns;

    private static object[] Rows(params object[][] rows) => rows;

    [Fact]
    public void Metadata_IsCorrect()
    {
        var sut = new RenderTableTool(new FakeBridge());
        sut.Name.Should().Be("render_table");
        sut.SupportedOperations.Should().BeEquivalentTo(["render"]);
        sut.Description.Should().Contain("columns");
    }

    [Fact]
    public async Task ExecuteAsync_UnknownOperation_Fails()
    {
        var bridge = new FakeBridge();
        var result = await new RenderTableTool(bridge).ExecuteAsync("explode", Args(("columns", Columns("A"))));
        result.Success.Should().BeFalse();
        bridge.InvokeCount.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_NoClientAttached_Fails()
    {
        var sut = new RenderTableTool(new FakeBridge { ClientAttached = false });
        var result = await sut.ExecuteAsync("render", Args(("columns", Columns("A"))));
        result.Success.Should().BeFalse();
        result.Error.Should().Contain("client");
    }

    [Fact]
    public async Task ExecuteAsync_MissingColumns_Fails()
    {
        var bridge = new FakeBridge();
        var result = await new RenderTableTool(bridge).ExecuteAsync("render", Args(("title", "Results")));
        result.Success.Should().BeFalse();
        result.Error.Should().Contain("columns");
        bridge.InvokeCount.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_EmptyColumns_Fails()
    {
        var bridge = new FakeBridge();
        var result = await new RenderTableTool(bridge).ExecuteAsync("render", Args(("columns", Array.Empty<object>())));
        result.Success.Should().BeFalse();
        bridge.InvokeCount.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_RowsNull_RelaysToClient()
    {
        // An explicit null for the optional 'rows' (LLMs routinely emit these) must not fail the table:
        // the client normalizes null to an empty table. The server validates columns only.
        var bridge = new FakeBridge { Result = "Displayed the table to the user." };
        var result = await new RenderTableTool(bridge).ExecuteAsync("render", Args(("columns", Columns("A")), ("rows", null)));
        result.Success.Should().BeTrue();
        bridge.InvokeCount.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_RowsNotAnArray_RelaysToClient()
    {
        // A malformed 'rows' is not rejected server-side; the client's parseTableArgs treats a non-array
        // as no rows. Server strictness must not exceed the client's leniency (they'd disagree otherwise).
        var bridge = new FakeBridge { Result = "Displayed the table to the user." };
        var result = await new RenderTableTool(bridge).ExecuteAsync("render", Args(("columns", Columns("A")), ("rows", "not-an-array")));
        result.Success.Should().BeTrue();
        bridge.InvokeCount.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_RaggedRows_RelaysToClient()
    {
        // One scalar amid valid rows must not fail the whole table — the client drops the bad row and
        // renders the rest. The server relays the ragged payload unchanged.
        var bridge = new FakeBridge { Result = "Displayed the table to the user." };
        var result = await new RenderTableTool(bridge).ExecuteAsync("render", Args(("columns", Columns("A")), ("rows", new object[] { "flat", new[] { "ok" } })));
        result.Success.Should().BeTrue();
        bridge.InvokeCount.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_ColumnsOnly_NoRows_Succeeds()
    {
        var bridge = new FakeBridge { Result = "Displayed the table to the user." };
        var result = await new RenderTableTool(bridge).ExecuteAsync("render", Args(("columns", Columns("Name", "Score"))));
        result.Success.Should().BeTrue();
        bridge.LastToolName.Should().Be("render_table");
    }

    [Fact]
    public async Task ExecuteAsync_HappyPath_PassesSerializedArgs_AndReturnsAck()
    {
        var bridge = new FakeBridge { Result = "Displayed the table to the user." };
        var result = await new RenderTableTool(bridge).ExecuteAsync("render", Args(
            ("title", "Scores"),
            ("columns", Columns("Name", "Score")),
            ("rows", Rows(["Ada", "97"], ["Alan", "91"]))));

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("Displayed the table");
        bridge.LastToolName.Should().Be("render_table");
        bridge.LastArgsJson.Should().Contain("Name").And.Contain("Ada").And.Contain("97");
    }

    [Fact]
    public async Task ExecuteAsync_BridgeTimeout_FailsGracefully()
    {
        var sut = new RenderTableTool(new FakeBridge { Throw = new TimeoutException() });
        var result = await sut.ExecuteAsync("render", Args(("columns", Columns("A"))));
        result.Success.Should().BeFalse();
    }

    private sealed class FakeBridge : IClientToolBridge
    {
        public bool ClientAttached { get; init; } = true;
        public string Result { get; init; } = "ok";
        public Exception? Throw { get; init; }

        public int InvokeCount { get; private set; }
        public string? LastToolName { get; private set; }
        public string? LastArgsJson { get; private set; }

        public bool IsClientAttached => ClientAttached;

        public Task<string> InvokeAsync(string toolName, string argumentsJson, CancellationToken cancellationToken = default)
        {
            InvokeCount++;
            LastToolName = toolName;
            LastArgsJson = argumentsJson;
            if (Throw is not null) return Task.FromException<string>(Throw);
            return Task.FromResult(Result);
        }
    }
}
