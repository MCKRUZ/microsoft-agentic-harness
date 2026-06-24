using Application.AI.Common.Services.Agent;
using Application.AI.Common.Services.Tools;
using Microsoft.Extensions.AI;
using Xunit;

namespace Application.AI.Common.Tests.Governance;

/// <summary>
/// Verifies the invocation-time wrapping decision used by <see cref="GoverningToolContextProvider"/>
/// to govern framework/progressive-disclosure tools that bypass the build-time wrap.
/// </summary>
public sealed class GoverningToolContextProviderTests
{
    private static AIFunction MakeFunction() => AIFunctionFactory.Create(
        () => "ok", new AIFunctionFactoryOptions { Name = "file_system", Description = "t" });

    [Fact]
    public void Govern_UnwrappedFunction_WrapsInGovernedAIFunction()
    {
        var inner = MakeFunction();

        var result = GoverningToolContextProvider.Govern(inner);

        Assert.IsType<GovernedAIFunction>(result);
        Assert.NotSame(inner, result);
        Assert.Equal(inner.Name, result.Name); // schema/name preserved by the decorator
    }

    [Fact]
    public void Govern_AlreadyGoverned_ReturnsSameInstance_NoDoubleWrap()
    {
        var alreadyGoverned = new GovernedAIFunction(MakeFunction());

        var result = GoverningToolContextProvider.Govern(alreadyGoverned);

        Assert.Same(alreadyGoverned, result);
    }
}
