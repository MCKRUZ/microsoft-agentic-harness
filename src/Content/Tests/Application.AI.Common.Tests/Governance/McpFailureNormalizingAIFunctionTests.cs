using Application.AI.Common.Services.Tools;
using Microsoft.Extensions.AI;
using Xunit;

namespace Application.AI.Common.Tests.Governance;

/// <summary>
/// Verifies <see cref="McpFailureNormalizingAIFunction"/> recognizes the MCP wire failure shape
/// (<c>isError</c> + <c>content</c>) and converts it to <see cref="ConvertedToolFailure"/>, while
/// leaving a genuine success untouched — the normalization #468 moved out of
/// <see cref="GovernedAIFunction"/> and into this dedicated wrapper.
/// </summary>
public sealed class McpFailureNormalizingAIFunctionTests
{
    /// <summary>
    /// Shaped like what an MCP-provided tool actually returns on failure — confirmed against the MCP
    /// C# SDK's <c>McpClientTool.InvokeCoreAsync</c> source: on <c>CallToolResult.IsError == true</c>
    /// it returns normally with <c>JsonSerializer.SerializeToElement(result, ...)</c>, never throws.
    /// The default marshaling every test here relies on already serializes this anonymous object into
    /// the same <see cref="System.Text.Json.JsonElement"/> shape the real SDK call produces.
    /// </summary>
    private static AIFunction MakeMcpFailingInner(string errorText) =>
        AIFunctionFactory.Create(
            () => new { isError = true, content = new[] { new { type = "text", text = errorText } } },
            new AIFunctionFactoryOptions { Name = "mcp_tool", Description = "test mcp tool" });

    [Fact]
    public async Task InvokeAsync_McpToolReportsIsError_ReturnsConvertedToolFailure()
    {
        var inner = MakeMcpFailingInner("Error: mcp boom");
        var normalizer = new McpFailureNormalizingAIFunction(inner);

        var result = await normalizer.InvokeAsync(new AIFunctionArguments(), CancellationToken.None);

        var failure = Assert.IsType<ConvertedToolFailure>(result);
        Assert.Equal("Error: mcp boom", failure.ErrorText);
    }

    [Fact]
    public async Task InvokeAsync_McpFailureWithNonObjectContentBlock_FallsBackToGenericMessage()
    {
        // Regression: JsonElement.TryGetProperty throws InvalidOperationException when the element's
        // ValueKind isn't Object (confirmed against the .NET docs) — a content array holding a bare
        // string, e.g. {"isError":true,"content":["oops"]}, used to crash this chokepoint instead of
        // falling through to the generic "no message" text every other malformed shape gets.
        var inner = AIFunctionFactory.Create(
            () => new { isError = true, content = new object[] { "a bare string, not a content block" } },
            new AIFunctionFactoryOptions { Name = "mcp_tool", Description = "test mcp tool" });
        var normalizer = new McpFailureNormalizingAIFunction(inner);

        var result = await normalizer.InvokeAsync(new AIFunctionArguments(), CancellationToken.None);

        var failure = Assert.IsType<ConvertedToolFailure>(result);
        Assert.Equal("MCP tool reported failure with no message.", failure.ErrorText);
    }

    [Fact]
    public async Task InvokeAsync_McpFailureWithNoContent_FallsBackToGenericMessage()
    {
        var inner = AIFunctionFactory.Create(
            () => new { isError = true },
            new AIFunctionFactoryOptions { Name = "mcp_tool", Description = "test mcp tool" });
        var normalizer = new McpFailureNormalizingAIFunction(inner);

        var result = await normalizer.InvokeAsync(new AIFunctionArguments(), CancellationToken.None);

        var failure = Assert.IsType<ConvertedToolFailure>(result);
        Assert.Equal("MCP tool reported failure with no message.", failure.ErrorText);
    }

    [Fact]
    public async Task InvokeAsync_JsonObjectSuccessWithoutIsError_PassesThroughUnchanged()
    {
        // Regression guard for the MCP-shape detection: a genuine structured success (not shaped like
        // an MCP failure) must never be misread as one just because it is a JsonElement.
        var inner = AIFunctionFactory.Create(
            () => new { status = "ok", value = 42 },
            new AIFunctionFactoryOptions { Name = "mcp_tool", Description = "test mcp tool" });
        var normalizer = new McpFailureNormalizingAIFunction(inner);

        var result = await normalizer.InvokeAsync(new AIFunctionArguments(), CancellationToken.None);

        Assert.IsNotType<ConvertedToolFailure>(result);
        var element = Assert.IsType<System.Text.Json.JsonElement>(result);
        Assert.Equal("ok", element.GetProperty("status").GetString());
    }

    [Fact]
    public async Task InvokeAsync_IsErrorFalse_PassesThroughUnchanged()
    {
        var inner = AIFunctionFactory.Create(
            () => new { isError = false, content = Array.Empty<object>() },
            new AIFunctionFactoryOptions { Name = "mcp_tool", Description = "test mcp tool" });
        var normalizer = new McpFailureNormalizingAIFunction(inner);

        var result = await normalizer.InvokeAsync(new AIFunctionArguments(), CancellationToken.None);

        Assert.IsNotType<ConvertedToolFailure>(result);
    }

    [Fact]
    public async Task InvokeAsync_McpFailureWithTypelessBlockCarryingText_SkipsItAndFallsBackToGenericMessage()
    {
        // #554: before this fix, the inner loop accepted any object with a "text" property, with no
        // "type" discriminator required — a fourth independent copy of "what counts as a content
        // block" that had drifted from ToolResultText.IsContentBlock (the predicate the outer
        // TryGetContentArray gate already enforces). Here the outer gate is satisfied by the
        // legitimate "image" block, so the array IS recognized as MCP-shaped, but the second block
        // (text with no type — not a real content block) must be skipped by the inner loop too, not
        // misread as the failure message.
        var inner = AIFunctionFactory.Create(
            () => new
            {
                isError = true,
                content = new object[]
                {
                    new { type = "image", data = "aGVsbG8=" },
                    new { text = "not a real content block, just an object with a text property" }
                }
            },
            new AIFunctionFactoryOptions { Name = "mcp_tool", Description = "test mcp tool" });
        var normalizer = new McpFailureNormalizingAIFunction(inner);

        var result = await normalizer.InvokeAsync(new AIFunctionArguments(), CancellationToken.None);

        var failure = Assert.IsType<ConvertedToolFailure>(result);
        Assert.Equal("MCP tool reported failure with no message.", failure.ErrorText);
    }

    [Fact]
    public void Decorator_PreservesInnerNameAndSchema()
    {
        var inner = MakeMcpFailingInner("irrelevant");
        var normalizer = new McpFailureNormalizingAIFunction(inner);

        Assert.Equal(inner.Name, normalizer.Name);
        Assert.Equal(inner.JsonSchema.ToString(), normalizer.JsonSchema.ToString());
    }
}
