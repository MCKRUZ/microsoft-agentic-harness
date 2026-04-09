using System.Net;
using System.Text.Json;
using Domain.Common.Config;
using Domain.Common.Config.Connectors;
using FluentAssertions;
using Infrastructure.AI.Connectors.Jira;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;
using Xunit;

namespace Infrastructure.AI.Connectors.Tests.Jira;

public class JiraIssuesConnectorTests
{
    #region Test Infrastructure

    private readonly Mock<HttpMessageHandler> _mockHandler = new();
    private readonly Mock<IOptionsMonitor<AppConfig>> _appConfigMonitor = new();

    private JiraIssuesConnector CreateConnector(
        string? baseUrl = "https://test.atlassian.net",
        string? email = "test@example.com",
        string? apiToken = "test-api-token",
        string? defaultProject = "TEST",
        int timeoutSeconds = 30)
    {
        var config = new AppConfig
        {
            Connectors = new ConnectorsConfig
            {
                Jira = new JiraConfig
                {
                    BaseUrl = baseUrl,
                    Email = email,
                    ApiToken = apiToken,
                    DefaultProject = defaultProject,
                    TimeoutSeconds = timeoutSeconds
                }
            }
        };
        _appConfigMonitor.Setup(m => m.CurrentValue).Returns(config);

        var httpClient = new HttpClient(_mockHandler.Object);
        var httpFactory = new Mock<IHttpClientFactory>();
        httpFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

        return new JiraIssuesConnector(
            NullLogger<JiraIssuesConnector>.Instance,
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
    public void IsAvailable_WhenFullyConfigured_ReturnsTrue()
    {
        var connector = CreateConnector();

        connector.IsAvailable.Should().BeTrue();
    }

    [Fact]
    public void IsAvailable_WhenMissingBaseUrl_ReturnsFalse()
    {
        var connector = CreateConnector(baseUrl: null);

        connector.IsAvailable.Should().BeFalse();
    }

    [Fact]
    public void IsAvailable_WhenMissingEmail_ReturnsFalse()
    {
        var connector = CreateConnector(email: null);

        connector.IsAvailable.Should().BeFalse();
    }

    [Fact]
    public void IsAvailable_WhenMissingApiToken_ReturnsFalse()
    {
        var connector = CreateConnector(apiToken: null);

        connector.IsAvailable.Should().BeFalse();
    }

    [Fact]
    public void SupportedOperations_ReturnsExpectedSet()
    {
        var connector = CreateConnector();

        connector.SupportedOperations.Should().BeEquivalentTo(
            ["list_issues", "create_issue", "update_issue", "transition_issue"]);
    }

    [Fact]
    public void ToolName_ReturnsExpectedValue()
    {
        var connector = CreateConnector();

        connector.ToolName.Should().Be("jira_issues");
    }

    #endregion

    #region ValidateParametersAsync

    [Fact]
    public async Task ValidateParametersAsync_ListIssues_MissingProjectAndNoDefault_ReturnsError()
    {
        var connector = CreateConnector(defaultProject: null);
        var parameters = new Dictionary<string, object>();

        var errors = await connector.ValidateParametersAsync("list_issues", parameters);

        errors.Should().Contain(e => e.Contains("Project"));
    }

    [Fact]
    public async Task ValidateParametersAsync_ListIssues_DefaultProjectUsed_ReturnsNoErrors()
    {
        var connector = CreateConnector(defaultProject: "DEFAULT");
        var parameters = new Dictionary<string, object>();

        var errors = await connector.ValidateParametersAsync("list_issues", parameters);

        errors.Should().BeEmpty();
    }

    [Fact]
    public async Task ValidateParametersAsync_CreateIssue_MissingSummaryAndIssueType_ReturnsErrors()
    {
        var connector = CreateConnector();
        var parameters = new Dictionary<string, object>();

        var errors = await connector.ValidateParametersAsync("create_issue", parameters);

        errors.Should().Contain(e => e.Contains("summary"));
        errors.Should().Contain(e => e.Contains("Issue type"));
    }

    [Fact]
    public async Task ValidateParametersAsync_CreateIssue_AllParamsPresent_ReturnsNoErrors()
    {
        var connector = CreateConnector();
        var parameters = new Dictionary<string, object>
        {
            ["summary"] = "Bug in login",
            ["issueType"] = "Bug"
        };

        var errors = await connector.ValidateParametersAsync("create_issue", parameters);

        errors.Should().BeEmpty();
    }

    [Fact]
    public async Task ValidateParametersAsync_UpdateIssue_MissingIssueIdOrKey_ReturnsError()
    {
        var connector = CreateConnector();
        var parameters = new Dictionary<string, object>();

        var errors = await connector.ValidateParametersAsync("update_issue", parameters);

        errors.Should().Contain(e => e.Contains("Issue ID or Key"));
    }

    [Fact]
    public async Task ValidateParametersAsync_TransitionIssue_MissingRequiredParams_ReturnsErrors()
    {
        var connector = CreateConnector();
        var parameters = new Dictionary<string, object>();

        var errors = await connector.ValidateParametersAsync("transition_issue", parameters);

        errors.Should().Contain(e => e.Contains("Issue ID or Key"));
        errors.Should().Contain(e => e.Contains("Transition"));
    }

    [Fact]
    public async Task ValidateParametersAsync_TransitionIssue_AllParamsPresent_ReturnsNoErrors()
    {
        var connector = CreateConnector();
        var parameters = new Dictionary<string, object>
        {
            ["issueIdOrKey"] = "TEST-123",
            ["transition"] = "Done"
        };

        var errors = await connector.ValidateParametersAsync("transition_issue", parameters);

        errors.Should().BeEmpty();
    }

    #endregion

    #region ListIssues

    [Fact]
    public async Task ListIssues_ValidParams_ReturnsSuccessWithIssues()
    {
        var connector = CreateConnector();
        var jsonResponse = JsonSerializer.Serialize(new
        {
            total = 2,
            issues = new[]
            {
                new
                {
                    key = "TEST-1",
                    fields = new
                    {
                        summary = "First issue",
                        status = new { name = "Open" },
                        assignee = new { displayName = "John Doe" },
                        priority = new { name = "High" },
                        issuetype = new { name = "Bug" }
                    }
                },
                new
                {
                    key = "TEST-2",
                    fields = new
                    {
                        summary = "Second issue",
                        status = new { name = "In Progress" },
                        assignee = new { displayName = "Jane Smith" },
                        priority = new { name = "Medium" },
                        issuetype = new { name = "Task" }
                    }
                }
            }
        });
        SetupHttpResponse(HttpStatusCode.OK, jsonResponse);

        var parameters = new Dictionary<string, object>();
        var result = await connector.ExecuteAsync("list_issues", parameters);

        result.IsSuccess.Should().BeTrue();
        result.MarkdownResult.Should().Contain("TEST-1");
        result.MarkdownResult.Should().Contain("First issue");
    }

    [Fact]
    public async Task ListIssues_EmptyResult_ReturnsSuccessWithNoIssues()
    {
        var connector = CreateConnector();
        var jsonResponse = JsonSerializer.Serialize(new { total = 0, issues = Array.Empty<object>() });
        SetupHttpResponse(HttpStatusCode.OK, jsonResponse);

        var parameters = new Dictionary<string, object>();
        var result = await connector.ExecuteAsync("list_issues", parameters);

        result.IsSuccess.Should().BeTrue();
        result.MarkdownResult.Should().Contain("No issues found");
    }

    [Fact]
    public async Task ListIssues_HttpError_ReturnsFailure()
    {
        var connector = CreateConnector();
        SetupHttpResponse(HttpStatusCode.Unauthorized, "Authentication failed");

        var parameters = new Dictionary<string, object>();
        var result = await connector.ExecuteAsync("list_issues", parameters);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Unauthorized");
    }

    [Fact]
    public async Task ListIssues_NetworkError_ReturnsFailure()
    {
        var connector = CreateConnector();
        SetupHttpException(new HttpRequestException("Connection refused"));

        var parameters = new Dictionary<string, object>();
        var result = await connector.ExecuteAsync("list_issues", parameters);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Network error");
    }

    [Fact]
    public async Task ListIssues_InvalidJson_ReturnsFailure()
    {
        var connector = CreateConnector();
        SetupHttpResponse(HttpStatusCode.OK, "not valid json");

        var parameters = new Dictionary<string, object>();
        var result = await connector.ExecuteAsync("list_issues", parameters);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("parsing error");
    }

    #endregion

    #region CreateIssue

    [Fact]
    public async Task CreateIssue_ValidParams_ReturnsSuccessWithIssueKey()
    {
        var connector = CreateConnector();
        var jsonResponse = JsonSerializer.Serialize(new
        {
            id = "10001",
            key = "TEST-42",
            self = "https://test.atlassian.net/rest/api/3/issue/10001"
        });
        SetupHttpResponse(HttpStatusCode.Created, jsonResponse);

        var parameters = new Dictionary<string, object>
        {
            ["summary"] = "New bug report",
            ["issueType"] = "Bug"
        };
        var result = await connector.ExecuteAsync("create_issue", parameters);

        result.IsSuccess.Should().BeTrue();
        result.MarkdownResult.Should().Contain("TEST-42");
        result.MarkdownResult.Should().Contain("Jira Issue Created");
    }

    [Fact]
    public async Task CreateIssue_HttpError_ReturnsFailure()
    {
        var connector = CreateConnector();
        SetupHttpResponse(HttpStatusCode.BadRequest, "Invalid issue type");

        var parameters = new Dictionary<string, object>
        {
            ["summary"] = "Test",
            ["issueType"] = "InvalidType"
        };
        var result = await connector.ExecuteAsync("create_issue", parameters);

        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task CreateIssue_NetworkError_ReturnsFailure()
    {
        var connector = CreateConnector();
        SetupHttpException(new HttpRequestException("DNS resolution failed"));

        var parameters = new Dictionary<string, object>
        {
            ["summary"] = "Test",
            ["issueType"] = "Bug"
        };
        var result = await connector.ExecuteAsync("create_issue", parameters);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Network error");
    }

    [Fact]
    public async Task CreateIssue_InvalidJsonResponse_ReturnsFailure()
    {
        var connector = CreateConnector();
        SetupHttpResponse(HttpStatusCode.OK, "not json");

        var parameters = new Dictionary<string, object>
        {
            ["summary"] = "Test",
            ["issueType"] = "Bug"
        };
        var result = await connector.ExecuteAsync("create_issue", parameters);

        result.IsSuccess.Should().BeFalse();
    }

    #endregion

    #region UpdateIssue

    [Fact]
    public async Task UpdateIssue_ValidParams_ReturnsSuccess()
    {
        var connector = CreateConnector();
        SetupHttpResponse(HttpStatusCode.NoContent, "");

        var parameters = new Dictionary<string, object>
        {
            ["issueIdOrKey"] = "TEST-10",
            ["summary"] = "Updated summary"
        };
        var result = await connector.ExecuteAsync("update_issue", parameters);

        result.IsSuccess.Should().BeTrue();
        result.MarkdownResult.Should().Contain("Updated");
    }

    [Fact]
    public async Task UpdateIssue_NoFieldsToUpdate_ReturnsFailure()
    {
        var connector = CreateConnector();

        var parameters = new Dictionary<string, object>
        {
            ["issueIdOrKey"] = "TEST-10"
        };
        var result = await connector.ExecuteAsync("update_issue", parameters);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("At least one field");
    }

    [Fact]
    public async Task UpdateIssue_HttpError_ReturnsFailure()
    {
        var connector = CreateConnector();
        SetupHttpResponse(HttpStatusCode.NotFound, "Issue not found");

        var parameters = new Dictionary<string, object>
        {
            ["issueIdOrKey"] = "TEST-999",
            ["summary"] = "Updated"
        };
        var result = await connector.ExecuteAsync("update_issue", parameters);

        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task UpdateIssue_NetworkError_ReturnsFailure()
    {
        var connector = CreateConnector();
        SetupHttpException(new HttpRequestException("Timeout"));

        var parameters = new Dictionary<string, object>
        {
            ["issueIdOrKey"] = "TEST-10",
            ["summary"] = "Updated"
        };
        var result = await connector.ExecuteAsync("update_issue", parameters);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Network error");
    }

    #endregion

    #region TransitionIssue

    [Fact]
    public async Task TransitionIssue_WithTransitionName_ReturnsSuccess()
    {
        var connector = CreateConnector();
        SetupHttpResponse(HttpStatusCode.NoContent, "");

        var parameters = new Dictionary<string, object>
        {
            ["issueIdOrKey"] = "TEST-5",
            ["transition"] = "Done"
        };
        var result = await connector.ExecuteAsync("transition_issue", parameters);

        result.IsSuccess.Should().BeTrue();
        result.MarkdownResult.Should().Contain("Transitioned");
    }

    [Fact]
    public async Task TransitionIssue_WithTransitionId_ReturnsSuccess()
    {
        var connector = CreateConnector();
        SetupHttpResponse(HttpStatusCode.NoContent, "");

        var parameters = new Dictionary<string, object>
        {
            ["issueIdOrKey"] = "TEST-5",
            ["transition"] = "31"
        };
        var result = await connector.ExecuteAsync("transition_issue", parameters);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task TransitionIssue_HttpError_ReturnsFailure()
    {
        var connector = CreateConnector();
        SetupHttpResponse(HttpStatusCode.BadRequest, "Invalid transition");

        var parameters = new Dictionary<string, object>
        {
            ["issueIdOrKey"] = "TEST-5",
            ["transition"] = "InvalidTransition"
        };
        var result = await connector.ExecuteAsync("transition_issue", parameters);

        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task TransitionIssue_NetworkError_ReturnsFailure()
    {
        var connector = CreateConnector();
        SetupHttpException(new HttpRequestException("Connection reset"));

        var parameters = new Dictionary<string, object>
        {
            ["issueIdOrKey"] = "TEST-5",
            ["transition"] = "Done"
        };
        var result = await connector.ExecuteAsync("transition_issue", parameters);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Network error");
    }

    #endregion

    #region Unavailable Connector

    [Fact]
    public async Task ExecuteAsync_WhenUnavailable_ReturnsFailure()
    {
        var connector = CreateConnector(apiToken: null);

        var result = await connector.ExecuteAsync("list_issues", new Dictionary<string, object>());

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("not configured");
    }

    [Fact]
    public async Task ExecuteAsync_UnsupportedOperation_ReturnsFailure()
    {
        var connector = CreateConnector();

        var result = await connector.ExecuteAsync("delete_issue", new Dictionary<string, object>());

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("not supported");
    }

    #endregion
}
