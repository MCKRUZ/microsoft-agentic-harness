using Application.AI.Common.Interfaces.Telemetry;
using Application.AI.Common.Services.Agent;
using Application.AI.Common.Services.Tools;
using Domain.AI.Planner;
using Infrastructure.AI.Telemetry.Redaction;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Application.AI.Common.Tests.Governance;

/// <summary>
/// Verifies the two checks <see cref="GoverningToolContextProvider"/> applies to the
/// <c>AIContext.Tools</c> channel — the one route onto the model's callable surface that does not pass
/// through <see cref="ToolChainBuilder"/>: reserved plan-capability exclusion, and invocation-time
/// governance wrapping of framework/progressive-disclosure tools.
/// </summary>
public sealed class GoverningToolContextProviderTests
{
    private static readonly IContentRedactionFilter RedactionFilter = TestRedactionFilter.Instance;

    private static AIFunction MakeFunction(string name = "file_system") => AIFunctionFactory.Create(
        () => "ok", new AIFunctionFactoryOptions { Name = name, Description = "t" });

    [Fact]
    public void Govern_UnwrappedFunction_WrapsInGovernedAIFunction()
    {
        var inner = MakeFunction();

        var result = GoverningToolContextProvider.Govern(inner, RedactionFilter);

        Assert.IsType<GovernedAIFunction>(result);
        Assert.NotSame(inner, result);
        Assert.Equal(inner.Name, result.Name); // schema/name preserved by the decorator
    }

    [Fact]
    public void Govern_AlreadyGoverned_ReturnsSameInstance_NoDoubleWrap()
    {
        var alreadyGoverned = new GovernedAIFunction(MakeFunction(), RedactionFilter);

        var result = GoverningToolContextProvider.Govern(alreadyGoverned, RedactionFilter);

        Assert.Same(alreadyGoverned, result);
    }

    [Theory]
    [InlineData(PlanCapabilities.LlmCall)]
    [InlineData(PlanCapabilities.Retrieval)]
    [InlineData("RAG_Retrieval")] // case-insensitive: the envelope matches names that way too
    public void FilterAndGovern_ReservedName_IsDroppedFromTheContextChannel(string reservedName)
    {
        // ToolChainBuilder is not the only way a tool reaches the model — the framework merges
        // AIContext.Tools from providers. A reserved plan-capability name arriving down THAT channel
        // would be published to the model and would collide with the envelope's grant namespace, so it
        // must be dropped here too.
        var tools = new List<AITool> { MakeFunction(reservedName), MakeFunction("file_system") };

        var result = GoverningToolContextProvider.FilterAndGovern(
            tools, NullLogger<GoverningToolContextProviderTests>.Instance, RedactionFilter);

        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal("file_system", result[0].Name);
    }

    [Fact]
    public void FilterAndGovern_NoReservedNames_StillWrapsForGovernance()
    {
        var tools = new List<AITool> { MakeFunction() };

        var result = GoverningToolContextProvider.FilterAndGovern(
            tools, NullLogger<GoverningToolContextProviderTests>.Instance, RedactionFilter);

        Assert.NotNull(result);
        Assert.IsType<GovernedAIFunction>(Assert.Single(result));
    }

    [Fact]
    public void FilterAndGovern_AlreadyGovernedAndNoCollisions_ReportsNoChange()
    {
        // Null means "keep the existing AIContext" — nothing was dropped and nothing needed wrapping.
        var tools = new List<AITool> { new GovernedAIFunction(MakeFunction(), RedactionFilter) };

        var result = GoverningToolContextProvider.FilterAndGovern(
            tools, NullLogger<GoverningToolContextProviderTests>.Instance, RedactionFilter);

        Assert.Null(result);
    }

    [Fact]
    public void FilterAndGovern_NoTools_ReportsNoChange()
    {
        Assert.Null(GoverningToolContextProvider.FilterAndGovern(
            null, NullLogger<GoverningToolContextProviderTests>.Instance, RedactionFilter));
        Assert.Null(GoverningToolContextProvider.FilterAndGovern(
            [], NullLogger<GoverningToolContextProviderTests>.Instance, RedactionFilter));
    }
}
