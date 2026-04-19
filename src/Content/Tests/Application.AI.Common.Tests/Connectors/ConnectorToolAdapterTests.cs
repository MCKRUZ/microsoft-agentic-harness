using Application.AI.Common.Interfaces.Connectors;
using Domain.AI.Models;
using FluentAssertions;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.Connectors;

/// <summary>
/// Tests for <see cref="ConnectorToolAdapter"/> covering property delegation,
/// execution result translation, null parameter filtering, and error handling.
/// </summary>
public class ConnectorToolAdapterTests
{
    private static Mock<IConnectorClient> CreateMockConnector(
        string toolName = "github_issues",
        IReadOnlyList<string>? operations = null)
    {
        var mock = new Mock<IConnectorClient>();
        mock.Setup(c => c.ToolName).Returns(toolName);
        mock.Setup(c => c.SupportedOperations).Returns(operations ?? ["list", "create"]);
        return mock;
    }

    [Fact]
    public void Ctor_NullConnector_ThrowsArgumentNull()
    {
        var act = () => new ConnectorToolAdapter(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Name_DelegatesToConnectorToolName()
    {
        var connector = CreateMockConnector("github_repos");
        var adapter = new ConnectorToolAdapter(connector.Object);

        adapter.Name.Should().Be("github_repos");
    }

    [Fact]
    public void Description_ContainsConnectorToolName()
    {
        var connector = CreateMockConnector("slack_notifications");
        var adapter = new ConnectorToolAdapter(connector.Object);

        adapter.Description.Should().Contain("slack_notifications");
    }

    [Fact]
    public void SupportedOperations_DelegatesToConnector()
    {
        var ops = new List<string> { "send", "list_channels" };
        var connector = CreateMockConnector(operations: ops);
        var adapter = new ConnectorToolAdapter(connector.Object);

        adapter.SupportedOperations.Should().BeEquivalentTo(ops);
    }

    [Fact]
    public async Task ExecuteAsync_SuccessWithMarkdown_ReturnsMarkdownOutput()
    {
        var connector = CreateMockConnector();
        connector
            .Setup(c => c.ExecuteAsync("list", It.IsAny<Dictionary<string, object>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ConnectorOperationResult.Success(markdown: "## Issues\n- Bug #1"));

        var adapter = new ConnectorToolAdapter(connector.Object);
        var result = await adapter.ExecuteAsync("list", new Dictionary<string, object?>());

        result.Success.Should().BeTrue();
        result.Output.Should().Be("## Issues\n- Bug #1");
    }

    [Fact]
    public async Task ExecuteAsync_SuccessWithDataOnly_ReturnsDataToString()
    {
        var connector = CreateMockConnector();
        connector
            .Setup(c => c.ExecuteAsync("list", It.IsAny<Dictionary<string, object>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ConnectorOperationResult.Success(data: 42));

        var adapter = new ConnectorToolAdapter(connector.Object);
        var result = await adapter.ExecuteAsync("list", new Dictionary<string, object?>());

        result.Success.Should().BeTrue();
        result.Output.Should().Be("42");
    }

    [Fact]
    public async Task ExecuteAsync_SuccessWithNoDataOrMarkdown_ReturnsGenericMessage()
    {
        var connector = CreateMockConnector();
        connector
            .Setup(c => c.ExecuteAsync("list", It.IsAny<Dictionary<string, object>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ConnectorOperationResult.Success());

        var adapter = new ConnectorToolAdapter(connector.Object);
        var result = await adapter.ExecuteAsync("list", new Dictionary<string, object?>());

        result.Success.Should().BeTrue();
        result.Output.Should().Be("Operation completed successfully");
    }

    [Fact]
    public async Task ExecuteAsync_Failure_ReturnsFailResult()
    {
        var connector = CreateMockConnector();
        connector
            .Setup(c => c.ExecuteAsync("create", It.IsAny<Dictionary<string, object>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ConnectorOperationResult.Failure("Unauthorized", 401));

        var adapter = new ConnectorToolAdapter(connector.Object);
        var result = await adapter.ExecuteAsync("create", new Dictionary<string, object?>());

        result.Success.Should().BeFalse();
        result.Error.Should().Be("Unauthorized");
    }

    [Fact]
    public async Task ExecuteAsync_FailureWithNullError_ReturnsFallbackMessage()
    {
        var connector = CreateMockConnector();
        connector
            .Setup(c => c.ExecuteAsync("create", It.IsAny<Dictionary<string, object>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConnectorOperationResult { IsSuccess = false, ErrorMessage = null });

        var adapter = new ConnectorToolAdapter(connector.Object);
        var result = await adapter.ExecuteAsync("create", new Dictionary<string, object?>());

        result.Success.Should().BeFalse();
        result.Error.Should().Be("Connector operation failed");
    }

    [Fact]
    public async Task ExecuteAsync_FiltersNullValues_FromParameters()
    {
        var connector = CreateMockConnector();
        Dictionary<string, object>? capturedParams = null;
        connector
            .Setup(c => c.ExecuteAsync("list", It.IsAny<Dictionary<string, object>>(), It.IsAny<CancellationToken>()))
            .Callback<string, Dictionary<string, object>, CancellationToken>((_, p, _) => capturedParams = p)
            .ReturnsAsync(ConnectorOperationResult.Success());

        var adapter = new ConnectorToolAdapter(connector.Object);
        var parameters = new Dictionary<string, object?>
        {
            ["repo"] = "my-repo",
            ["filter"] = null,
            ["page"] = 1
        };

        await adapter.ExecuteAsync("list", parameters);

        capturedParams.Should().NotBeNull();
        capturedParams.Should().ContainKey("repo");
        capturedParams.Should().ContainKey("page");
        capturedParams.Should().NotContainKey("filter");
    }
}
