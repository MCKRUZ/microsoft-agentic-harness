using Application.AI.Common.Interfaces.Tools;
using Domain.AI.Models;
using Microsoft.Extensions.AI;

namespace Application.AI.Common.Tests;

/// <summary>
/// A minimal <see cref="ITool"/> that exists only to be registered under a name, for tests whose
/// subject is which tools reach the model rather than what any tool does.
/// </summary>
/// <remarks>
/// Shared rather than re-declared per test class because it implements a production interface: when
/// <see cref="ITool"/> gains a member, one copy fails to compile instead of several.
/// </remarks>
internal sealed class StubTool(string name) : ITool
{
    /// <inheritdoc />
    public string Name { get; } = name;

    /// <inheritdoc />
    public string Description => "stub";

    /// <inheritdoc />
    public IReadOnlyList<string> SupportedOperations { get; } = ["run"];

    /// <inheritdoc />
    public Task<ToolResult> ExecuteAsync(
        string operation,
        IReadOnlyDictionary<string, object?> parameters,
        CancellationToken cancellationToken = default) => Task.FromResult(ToolResult.Ok("r"));
}

/// <summary>
/// An <see cref="IToolConverter"/> that converts every tool, so a test sees the tool set the code under
/// test assembled rather than a set narrowed by conversion.
/// </summary>
/// <remarks>
/// <b>This double proves nothing about conversion.</b> A test asserting that a particular tool is
/// refused conversion must not use it — it accepts everything, so the assertion would pass while
/// checking nothing.
/// </remarks>
internal sealed class PassThroughToolConverter : IToolConverter
{
    /// <inheritdoc />
    public int Priority => 100;

    /// <inheritdoc />
    public bool CanConvert(ITool tool) => true;

    /// <inheritdoc />
    public AITool? Convert(ITool tool, IReadOnlyList<string>? allowedOperations = null) =>
        AIFunctionFactory.Create(() => "r", tool.Name);
}
