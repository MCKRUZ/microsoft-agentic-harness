using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Services.Governance;
using Application.AI.Common.Services.Tools;
using Microsoft.Extensions.AI;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.Governance;

/// <summary>
/// Verifies the governed tool-function wrapper consults the ambient governor before invoking the
/// inner tool, blocks on denial without calling the tool, and passes through when ungoverned.
/// </summary>
public sealed class GovernedAIFunctionTests : IDisposable
{
    public void Dispose() => ToolGovernanceAccessor.Current = null;

    private static (AIFunction inner, Func<bool> wasInvoked) MakeInner()
    {
        var invoked = false;
        var inner = AIFunctionFactory.Create(
            () => { invoked = true; return "inner-result"; },
            new AIFunctionFactoryOptions { Name = "file_system", Description = "test tool" });
        return (inner, () => invoked);
    }

    [Fact]
    public async Task InvokeAsync_GovernorDenies_ReturnsDeniedMessageAndSkipsInner()
    {
        var (inner, wasInvoked) = MakeInner();
        var governor = new Mock<IToolInvocationGovernor>();
        governor
            .Setup(g => g.AuthorizeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ToolInvocationDecision.Deny("Error: tool 'file_system' was blocked by governance — denied."));
        ToolGovernanceAccessor.Current = governor.Object;

        var governed = new GovernedAIFunction(inner);
        var result = await governed.InvokeAsync(new AIFunctionArguments(), CancellationToken.None);

        Assert.False(wasInvoked(), "inner tool must not run when governance denies");
        Assert.Contains("blocked by governance", result?.ToString());
    }

    [Fact]
    public async Task InvokeAsync_GovernorAllows_InvokesInner()
    {
        var (inner, wasInvoked) = MakeInner();
        var governor = new Mock<IToolInvocationGovernor>();
        governor
            .Setup(g => g.AuthorizeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ToolInvocationDecision.Allow());
        ToolGovernanceAccessor.Current = governor.Object;

        var governed = new GovernedAIFunction(inner);
        await governed.InvokeAsync(new AIFunctionArguments(), CancellationToken.None);

        Assert.True(wasInvoked(), "inner tool must run when governance allows");
    }

    [Fact]
    public async Task InvokeAsync_NoAmbientGovernor_PassesThrough()
    {
        var (inner, wasInvoked) = MakeInner();
        ToolGovernanceAccessor.Current = null;

        var governed = new GovernedAIFunction(inner);
        await governed.InvokeAsync(new AIFunctionArguments(), CancellationToken.None);

        Assert.True(wasInvoked(), "inner tool must run when no governor is ambient");
    }

    [Fact]
    public void Decorator_PreservesInnerNameAndSchema()
    {
        var (inner, _) = MakeInner();
        var governed = new GovernedAIFunction(inner);

        Assert.Equal(inner.Name, governed.Name);
        Assert.Equal(inner.JsonSchema.ToString(), governed.JsonSchema.ToString());
    }
}
