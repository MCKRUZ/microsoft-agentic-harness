using Application.AI.Common.Interfaces.Connectors;
using FluentAssertions;
using Infrastructure.AI.Connectors.Core;
using Moq;
using Xunit;

namespace Infrastructure.AI.Connectors.Tests.Core;

public class ConnectorClientFactoryTests
{
    private static Mock<IConnectorClient> CreateMockClient(
        string toolName, bool isAvailable = true)
    {
        var mock = new Mock<IConnectorClient>();
        mock.Setup(c => c.ToolName).Returns(toolName);
        mock.Setup(c => c.IsAvailable).Returns(isAvailable);
        return mock;
    }

    [Fact]
    public void GetClient_KnownToolName_ReturnsClient()
    {
        var client = CreateMockClient("github_issues").Object;
        var factory = new ConnectorClientFactory([client]);

        var result = factory.GetClient("github_issues");

        result.Should().BeSameAs(client);
    }

    [Fact]
    public void GetClient_UnknownToolName_ReturnsNull()
    {
        var client = CreateMockClient("github_issues").Object;
        var factory = new ConnectorClientFactory([client]);

        var result = factory.GetClient("nonexistent_tool");

        result.Should().BeNull();
    }

    [Fact]
    public void GetClient_IsCaseInsensitive()
    {
        var client = CreateMockClient("github_issues").Object;
        var factory = new ConnectorClientFactory([client]);

        var result = factory.GetClient("GitHub_Issues");

        result.Should().BeSameAs(client);
    }

    [Fact]
    public void GetAllClients_ReturnsAllRegistered()
    {
        var available = CreateMockClient("github_issues", isAvailable: true).Object;
        var unavailable = CreateMockClient("jira_issues", isAvailable: false).Object;
        var factory = new ConnectorClientFactory([available, unavailable]);

        var result = factory.GetAllClients();

        result.Should().HaveCount(2);
        result.Should().Contain(available);
        result.Should().Contain(unavailable);
    }

    [Fact]
    public void GetAvailableClients_FiltersUnavailable()
    {
        var available = CreateMockClient("github_issues", isAvailable: true).Object;
        var unavailable = CreateMockClient("jira_issues", isAvailable: false).Object;
        var factory = new ConnectorClientFactory([available, unavailable]);

        var result = factory.GetAvailableClients();

        result.Should().ContainSingle()
            .Which.Should().BeSameAs(available);
    }

    [Fact]
    public void GetAvailableClients_EmptyWhenNoneAvailable()
    {
        var unavailable = CreateMockClient("jira_issues", isAvailable: false).Object;
        var factory = new ConnectorClientFactory([unavailable]);

        var result = factory.GetAvailableClients();

        result.Should().BeEmpty();
    }
}
