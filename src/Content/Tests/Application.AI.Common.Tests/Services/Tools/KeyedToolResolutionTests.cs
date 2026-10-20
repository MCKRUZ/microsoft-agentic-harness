using Application.AI.Common.Exceptions;
using Application.AI.Common.Interfaces.Tools;
using Application.AI.Common.Services.Tools;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Application.AI.Common.Tests.Services.Tools;

/// <summary>
/// <see cref="KeyedToolResolution"/> is the only way a keyed tool is built (#888), so the name check it carries is
/// what lets every grant and deny rule name a tool by one string.
/// </summary>
public sealed class KeyedToolResolutionTests
{
    private static IServiceProvider Register(string key, string reportedName)
    {
        var tool = new Mock<ITool>();
        tool.Setup(t => t.Name).Returns(reportedName);
        var services = new ServiceCollection();
        services.AddKeyedSingleton<ITool>(key, (_, _) => tool.Object);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Resolve_ToolNamedByItsKey_ReturnsTheTool()
    {
        var tool = KeyedToolResolution.Resolve(Register("file_system", "file_system"), "file_system");

        tool.Should().NotBeNull();
        tool!.Name.Should().Be("file_system");
    }

    [Fact]
    public void Resolve_NameDiffersOnlyInCase_IsTheSameToolAndResolves()
    {
        // Grants and deny rules compare names case-insensitively, so a casing difference names one tool.
        var tool = KeyedToolResolution.Resolve(Register("file_system", "File_System"), "file_system");

        tool.Should().NotBeNull();
    }

    [Fact]
    public void Resolve_NameDiffersFromKey_ThrowsCarryingBothNames()
    {
        var act = () => KeyedToolResolution.Resolve(Register("registered_key", "published_name"), "registered_key");

        var thrown = act.Should().Throw<ToolNameMismatchException>().Which;
        thrown.Key.Should().Be("registered_key");
        thrown.Name.Should().Be("published_name");
    }

    [Fact]
    public void Resolve_NothingRegisteredUnderTheKey_ReturnsNull()
    {
        var tool = KeyedToolResolution.Resolve(new ServiceCollection().BuildServiceProvider(), "absent");

        tool.Should().BeNull();
    }
}
