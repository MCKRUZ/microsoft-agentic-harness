using Domain.Common.Config;
using Domain.Common.Config.Connectors;
using FluentAssertions;
using Infrastructure.AI.Connectors.GitHub;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.Connectors.Tests.GitHub;

public class GitHubIssuesConnectorTests
{
    private static GitHubIssuesConnector CreateConnector(string? accessToken = null)
    {
        var config = new AppConfig
        {
            Connectors = new ConnectorsConfig
            {
                GitHub = new GitHubConfig { AccessToken = accessToken }
            }
        };

        var monitor = new Mock<IOptionsMonitor<AppConfig>>();
        monitor.Setup(m => m.CurrentValue).Returns(config);

        var httpFactory = new Mock<IHttpClientFactory>();

        return new GitHubIssuesConnector(
            NullLogger<GitHubIssuesConnector>.Instance,
            httpFactory.Object,
            monitor.Object);
    }

    [Fact]
    public void IsAvailable_WhenTokenConfigured_ReturnsTrue()
    {
        var connector = CreateConnector(accessToken: "ghp_test_token");

        connector.IsAvailable.Should().BeTrue();
    }

    [Fact]
    public void IsAvailable_WhenNoToken_ReturnsFalse()
    {
        var connector = CreateConnector(accessToken: null);

        connector.IsAvailable.Should().BeFalse();
    }

    [Fact]
    public void SupportedOperations_ReturnsExpectedSet()
    {
        var connector = CreateConnector();

        connector.SupportedOperations.Should().BeEquivalentTo(
            ["list_issues", "create_issue", "update_issue", "close_issue"]);
    }

    [Fact]
    public void ToolName_ReturnsExpectedValue()
    {
        var connector = CreateConnector();

        connector.ToolName.Should().Be("github_issues");
    }
}
