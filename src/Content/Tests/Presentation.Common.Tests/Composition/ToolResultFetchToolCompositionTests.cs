using Application.AI.Common.Interfaces.Tools;
using FluentAssertions;
using Infrastructure.AI.Tools;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Presentation.Common.Tests.Composition;

/// <summary>
/// Proves <c>tool_result_fetch</c> (#521) resolves on the REAL composition root, not just in a
/// hand-rolled test container. The plan for this package calls this out explicitly: no existing test
/// already covers "does this keyed tool resolve", so a deleted or mistyped registration line in
/// <c>Infrastructure.AI/DependencyInjection.Tools.cs</c> would otherwise ship silently — the model
/// would offer a marker naming a tool nothing can resolve.
/// </summary>
/// <remarks>
/// Resolved from a CREATED SCOPE, not the root provider — <see cref="ToolResultFetchTool"/> is
/// registered <c>AddKeyedScoped</c>, deliberately unlike most keyed tools (see its own remarks on
/// why a singleton registration would capture whichever caller's scope resolved it first). Resolving
/// it from the root under <see cref="CompositionRootTestHost"/>'s <c>ValidateScopes = true</c> would
/// itself throw, so a root-provider resolution here would prove the wrong thing.
/// </remarks>
public sealed class ToolResultFetchToolCompositionTests
{
    [Fact]
    public void ToolResultFetchTool_IsRegisteredOnTheProductionGraph()
    {
        using var provider = CompositionRootTestHost.BuildProvider(new Dictionary<string, string?>());
        using var scope = provider.CreateScope();

        var tool = scope.ServiceProvider.GetRequiredKeyedService<ITool>(ToolResultFetchTool.ToolName);

        tool.Should().BeOfType<ToolResultFetchTool>(
            "the model's only way to retrieve a spilled result is this exact keyed tool");
    }
}
