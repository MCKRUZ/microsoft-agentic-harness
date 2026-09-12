using Application.AI.Common.Interfaces.Plugins;
using FluentAssertions;
using Xunit;

namespace Application.AI.Common.Tests.Plugins;

/// <summary>
/// #611: <see cref="PluginToolBoundaryViolationExtensions.DescribeEntry"/> and
/// <see cref="PluginToolBoundaryViolationExtensions.DescribeWithPlugin"/> are the shared formatters
/// that replaced two independently hand-written strings in <c>McpToolProvider</c> and
/// <c>PluginToolBoundaryStartupValidator</c>, which had already drifted in tone/detail from each
/// other.
/// </summary>
public sealed class PluginToolBoundaryViolationExtensionsTests
{
    [Fact]
    public void DescribeEntry_FormatsListKindAndToolNameWithoutPluginName()
    {
        var violation = new PluginToolBoundaryViolation("my-plugin", PluginToolBoundaryListKind.DeniedTools, "some_tool");

        violation.DescribeEntry().Should().Be("DeniedTools entry 'some_tool'");
    }

    [Fact]
    public void DescribeEntry_AllowedToolsListKind_IsReflectedInOutput()
    {
        var violation = new PluginToolBoundaryViolation("my-plugin", PluginToolBoundaryListKind.AllowedTools, "other_tool");

        violation.DescribeEntry().Should().Be("AllowedTools entry 'other_tool'");
    }

    [Fact]
    public void DescribeWithPlugin_PrefixesDescribeEntryWithThePluginName()
    {
        var violation = new PluginToolBoundaryViolation("my-plugin", PluginToolBoundaryListKind.DeniedTools, "some_tool");

        violation.DescribeWithPlugin().Should().Be("Plugin 'my-plugin': DeniedTools entry 'some_tool'");
    }
}
