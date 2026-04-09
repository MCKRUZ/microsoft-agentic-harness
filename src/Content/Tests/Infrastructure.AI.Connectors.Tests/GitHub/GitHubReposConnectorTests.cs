using System.Net;
using System.Text.Json;
using Domain.Common.Config;
using Domain.Common.Config.Connectors;
using FluentAssertions;
using Infrastructure.AI.Connectors.GitHub;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;
using Xunit;

namespace Infrastructure.AI.Connectors.Tests.GitHub;

public class GitHubReposConnectorTests
{
    #region Test Infrastructure

    private readonly Mock<HttpMessageHandler> _mockHandler = new();
    private readonly Mock<IOptionsMonitor<AppConfig>> _appConfigMonitor = new();

    private GitHubReposConnector CreateConnector(
        string? accessToken = "test-token",
        string baseUrl = "https://api.github.com",
        string? defaultOwner = "test-owner",
        int timeoutSeconds = 30)
    {
        var config = new AppConfig
        {
            Connectors = new ConnectorsConfig
            {
                GitHub = new GitHubConfig
                {
                    AccessToken = accessToken,
                    BaseUrl = baseUrl,
                    DefaultOwner = defaultOwner,
                    TimeoutSeconds = timeoutSeconds
                }
            }
        };
        _appConfigMonitor.Setup(m => m.CurrentValue).Returns(config);

        var httpClient = new HttpClient(_mockHandler.Object);
        var httpFactory = new Mock<IHttpClientFactory>();
        httpFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

        return new GitHubReposConnector(
            NullLogger<GitHubReposConnector>.Instance,
            httpFactory.Object,
            _appConfigMonitor.Object);
    }

    private void SetupHttpResponse(HttpStatusCode statusCode, string content)
    {
        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(content)
            });
    }

    private void SetupHttpException(HttpRequestException exception)
    {
        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(exception);
    }

    #endregion

    #region Properties

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
            ["create_repository", "configure_settings", "add_collaborator", "list_repositories"]);
    }

    [Fact]
    public void ToolName_ReturnsExpectedValue()
    {
        var connector = CreateConnector();

        connector.ToolName.Should().Be("github_repos");
    }

    #endregion

    #region ValidateParametersAsync

    [Fact]
    public async Task ValidateParametersAsync_CreateRepository_MissingName_ReturnsError()
    {
        var connector = CreateConnector();
        var parameters = new Dictionary<string, object>();

        var errors = await connector.ValidateParametersAsync("create_repository", parameters);

        errors.Should().Contain(e => e.Contains("name"));
    }

    [Fact]
    public async Task ValidateParametersAsync_CreateRepository_WithName_ReturnsNoErrors()
    {
        var connector = CreateConnector();
        var parameters = new Dictionary<string, object> { ["name"] = "new-repo" };

        var errors = await connector.ValidateParametersAsync("create_repository", parameters);

        errors.Should().BeEmpty();
    }

    [Fact]
    public async Task ValidateParametersAsync_ConfigureSettings_MissingOwnerAndNoDefault_ReturnsError()
    {
        var connector = CreateConnector(defaultOwner: null);
        var parameters = new Dictionary<string, object> { ["repo"] = "my-repo" };

        var errors = await connector.ValidateParametersAsync("configure_settings", parameters);

        errors.Should().Contain(e => e.Contains("Owner"));
    }

    [Fact]
    public async Task ValidateParametersAsync_ConfigureSettings_MissingRepo_ReturnsError()
    {
        var connector = CreateConnector();
        var parameters = new Dictionary<string, object>();

        var errors = await connector.ValidateParametersAsync("configure_settings", parameters);

        errors.Should().Contain(e => e.Contains("Repository"));
    }

    [Fact]
    public async Task ValidateParametersAsync_AddCollaborator_MissingUsername_ReturnsError()
    {
        var connector = CreateConnector();
        var parameters = new Dictionary<string, object> { ["repo"] = "my-repo" };

        var errors = await connector.ValidateParametersAsync("add_collaborator", parameters);

        errors.Should().Contain(e => e.Contains("username"));
    }

    [Fact]
    public async Task ValidateParametersAsync_AddCollaborator_InvalidPermission_ReturnsError()
    {
        var connector = CreateConnector();
        var parameters = new Dictionary<string, object>
        {
            ["repo"] = "my-repo",
            ["username"] = "user1",
            ["permission"] = "superadmin"
        };

        var errors = await connector.ValidateParametersAsync("add_collaborator", parameters);

        errors.Should().Contain(e => e.Contains("Invalid permission"));
    }

    [Fact]
    public async Task ValidateParametersAsync_AddCollaborator_ValidPermission_ReturnsNoErrors()
    {
        var connector = CreateConnector();
        var parameters = new Dictionary<string, object>
        {
            ["repo"] = "my-repo",
            ["username"] = "user1",
            ["permission"] = "push"
        };

        var errors = await connector.ValidateParametersAsync("add_collaborator", parameters);

        errors.Should().BeEmpty();
    }

    [Fact]
    public async Task ValidateParametersAsync_ListRepositories_NoParamsRequired_ReturnsNoErrors()
    {
        var connector = CreateConnector();
        var parameters = new Dictionary<string, object>();

        var errors = await connector.ValidateParametersAsync("list_repositories", parameters);

        errors.Should().BeEmpty();
    }

    #endregion

    #region CreateRepository

    [Fact]
    public async Task CreateRepository_ValidParams_ReturnsSuccessWithRepoDetails()
    {
        var connector = CreateConnector();
        var jsonResponse = JsonSerializer.Serialize(new
        {
            name = "new-repo",
            full_name = "test-owner/new-repo",
            html_url = "https://github.com/test-owner/new-repo",
            clone_url = "https://github.com/test-owner/new-repo.git",
            @private = false
        });
        SetupHttpResponse(HttpStatusCode.Created, jsonResponse);

        var parameters = new Dictionary<string, object> { ["name"] = "new-repo" };
        var result = await connector.ExecuteAsync("create_repository", parameters);

        result.IsSuccess.Should().BeTrue();
        result.MarkdownResult.Should().Contain("new-repo");
        result.MarkdownResult.Should().Contain("Repository Created");
    }

    [Fact]
    public async Task CreateRepository_HttpError_ReturnsFailure()
    {
        var connector = CreateConnector();
        SetupHttpResponse(HttpStatusCode.UnprocessableEntity, "Repository already exists");

        var parameters = new Dictionary<string, object> { ["name"] = "existing-repo" };
        var result = await connector.ExecuteAsync("create_repository", parameters);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("UnprocessableEntity");
    }

    [Fact]
    public async Task CreateRepository_NetworkError_ReturnsFailure()
    {
        var connector = CreateConnector();
        SetupHttpException(new HttpRequestException("Connection refused"));

        var parameters = new Dictionary<string, object> { ["name"] = "new-repo" };
        var result = await connector.ExecuteAsync("create_repository", parameters);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Network error");
    }

    [Fact]
    public async Task CreateRepository_InvalidJsonResponse_ReturnsFailure()
    {
        var connector = CreateConnector();
        SetupHttpResponse(HttpStatusCode.OK, "not-json");

        var parameters = new Dictionary<string, object> { ["name"] = "new-repo" };
        var result = await connector.ExecuteAsync("create_repository", parameters);

        result.IsSuccess.Should().BeFalse();
    }

    #endregion

    #region ConfigureSettings

    [Fact]
    public async Task ConfigureSettings_ValidParams_ReturnsSuccess()
    {
        var connector = CreateConnector();
        var jsonResponse = JsonSerializer.Serialize(new
        {
            full_name = "test-owner/my-repo",
            html_url = "https://github.com/test-owner/my-repo"
        });
        SetupHttpResponse(HttpStatusCode.OK, jsonResponse);

        var parameters = new Dictionary<string, object>
        {
            ["repo"] = "my-repo",
            ["description"] = "Updated description"
        };
        var result = await connector.ExecuteAsync("configure_settings", parameters);

        result.IsSuccess.Should().BeTrue();
        result.MarkdownResult.Should().Contain("Settings Updated");
    }

    [Fact]
    public async Task ConfigureSettings_NoSettingsToUpdate_ReturnsFailure()
    {
        var connector = CreateConnector();

        var parameters = new Dictionary<string, object> { ["repo"] = "my-repo" };
        var result = await connector.ExecuteAsync("configure_settings", parameters);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("At least one setting");
    }

    [Fact]
    public async Task ConfigureSettings_HttpError_ReturnsFailure()
    {
        var connector = CreateConnector();
        SetupHttpResponse(HttpStatusCode.NotFound, "Not Found");

        var parameters = new Dictionary<string, object>
        {
            ["repo"] = "nonexistent",
            ["description"] = "test"
        };
        var result = await connector.ExecuteAsync("configure_settings", parameters);

        result.IsSuccess.Should().BeFalse();
    }

    #endregion

    #region AddCollaborator

    [Fact]
    public async Task AddCollaborator_ValidParams_ReturnsSuccess()
    {
        var connector = CreateConnector();
        SetupHttpResponse(HttpStatusCode.Created, "{}");

        var parameters = new Dictionary<string, object>
        {
            ["repo"] = "my-repo",
            ["username"] = "collaborator1",
            ["permission"] = "push"
        };
        var result = await connector.ExecuteAsync("add_collaborator", parameters);

        result.IsSuccess.Should().BeTrue();
        result.MarkdownResult.Should().Contain("Collaborator Added");
        result.MarkdownResult.Should().Contain("collaborator1");
    }

    [Fact]
    public async Task AddCollaborator_NoContent_ReturnsSuccess()
    {
        var connector = CreateConnector();
        SetupHttpResponse(HttpStatusCode.NoContent, "");

        var parameters = new Dictionary<string, object>
        {
            ["repo"] = "my-repo",
            ["username"] = "collaborator1"
        };
        var result = await connector.ExecuteAsync("add_collaborator", parameters);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task AddCollaborator_HttpError_ReturnsFailure()
    {
        var connector = CreateConnector();
        SetupHttpResponse(HttpStatusCode.Forbidden, "Forbidden");

        var parameters = new Dictionary<string, object>
        {
            ["repo"] = "my-repo",
            ["username"] = "unauthorized-user"
        };
        var result = await connector.ExecuteAsync("add_collaborator", parameters);

        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task AddCollaborator_NetworkError_ReturnsFailure()
    {
        var connector = CreateConnector();
        SetupHttpException(new HttpRequestException("Timeout"));

        var parameters = new Dictionary<string, object>
        {
            ["repo"] = "my-repo",
            ["username"] = "user1"
        };
        var result = await connector.ExecuteAsync("add_collaborator", parameters);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Network error");
    }

    #endregion

    #region ListRepositories

    [Fact]
    public async Task ListRepositories_ValidParams_ReturnsSuccessWithRepos()
    {
        var connector = CreateConnector();
        var jsonResponse = JsonSerializer.Serialize(new[]
        {
            new
            {
                name = "repo1",
                full_name = "test-owner/repo1",
                @private = false,
                description = "First repo",
                language = "C#",
                stargazers_count = 10,
                html_url = "https://github.com/test-owner/repo1"
            }
        });
        SetupHttpResponse(HttpStatusCode.OK, jsonResponse);

        var parameters = new Dictionary<string, object>();
        var result = await connector.ExecuteAsync("list_repositories", parameters);

        result.IsSuccess.Should().BeTrue();
        result.MarkdownResult.Should().Contain("repo1");
    }

    [Fact]
    public async Task ListRepositories_EmptyResponse_ReturnsSuccessWithNoRepos()
    {
        var connector = CreateConnector();
        SetupHttpResponse(HttpStatusCode.OK, "[]");

        var parameters = new Dictionary<string, object>();
        var result = await connector.ExecuteAsync("list_repositories", parameters);

        result.IsSuccess.Should().BeTrue();
        result.MarkdownResult.Should().Contain("No repositories found");
    }

    [Fact]
    public async Task ListRepositories_HttpError_ReturnsFailure()
    {
        var connector = CreateConnector();
        SetupHttpResponse(HttpStatusCode.Unauthorized, "Bad credentials");

        var parameters = new Dictionary<string, object>();
        var result = await connector.ExecuteAsync("list_repositories", parameters);

        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task ListRepositories_NetworkError_ReturnsFailure()
    {
        var connector = CreateConnector();
        SetupHttpException(new HttpRequestException("Connection refused"));

        var parameters = new Dictionary<string, object>();
        var result = await connector.ExecuteAsync("list_repositories", parameters);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Network error");
    }

    [Fact]
    public async Task ListRepositories_InvalidJson_ReturnsFailure()
    {
        var connector = CreateConnector();
        SetupHttpResponse(HttpStatusCode.OK, "invalid json");

        var parameters = new Dictionary<string, object>();
        var result = await connector.ExecuteAsync("list_repositories", parameters);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("parsing error");
    }

    #endregion

    #region Unavailable Connector

    [Fact]
    public async Task ExecuteAsync_WhenUnavailable_ReturnsFailure()
    {
        var connector = CreateConnector(accessToken: null);

        var result = await connector.ExecuteAsync("create_repository", new Dictionary<string, object>());

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("not configured");
    }

    [Fact]
    public async Task ExecuteAsync_UnsupportedOperation_ReturnsFailure()
    {
        var connector = CreateConnector();

        var result = await connector.ExecuteAsync("delete_repository", new Dictionary<string, object>());

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("not supported");
    }

    #endregion
}
