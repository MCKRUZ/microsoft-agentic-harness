using System.Net;
using System.Text.Json;
using Domain.Common.Config;
using Domain.Common.Config.Connectors;
using FluentAssertions;
using Infrastructure.AI.Connectors.AzureDevOps;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;
using Xunit;

namespace Infrastructure.AI.Connectors.Tests.AzureDevOps;

public class AzureDevOpsWorkItemsConnectorTests
{
    #region Test Infrastructure

    private readonly Mock<HttpMessageHandler> _mockHandler = new();
    private readonly Mock<IOptionsMonitor<AppConfig>> _appConfigMonitor = new();

    private AzureDevOpsWorkItemsConnector CreateConnector(
        string? organizationUrl = "https://dev.azure.com/test-org",
        string? personalAccessToken = "test-pat",
        string? defaultProject = "TestProject",
        string apiVersion = "7.1-preview.3",
        int timeoutSeconds = 30)
    {
        var config = new AppConfig
        {
            Connectors = new ConnectorsConfig
            {
                AzureDevOps = new AzureDevOpsConfig
                {
                    OrganizationUrl = organizationUrl,
                    PersonalAccessToken = personalAccessToken,
                    DefaultProject = defaultProject,
                    ApiVersion = apiVersion,
                    TimeoutSeconds = timeoutSeconds
                }
            }
        };
        _appConfigMonitor.Setup(m => m.CurrentValue).Returns(config);

        var httpClient = new HttpClient(_mockHandler.Object);
        var httpFactory = new Mock<IHttpClientFactory>();
        httpFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

        return new AzureDevOpsWorkItemsConnector(
            NullLogger<AzureDevOpsWorkItemsConnector>.Instance,
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

    private void SetupHttpSequence(params (HttpStatusCode statusCode, string content)[] responses)
    {
        var setup = _mockHandler.Protected()
            .SetupSequence<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>());

        foreach (var (statusCode, content) in responses)
        {
            setup.ReturnsAsync(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(content)
            });
        }
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
    public void IsAvailable_WhenMissingOrgUrl_ReturnsFalse()
    {
        var connector = CreateConnector(organizationUrl: null);

        connector.IsAvailable.Should().BeFalse();
    }

    [Fact]
    public void IsAvailable_WhenMissingPAT_ReturnsFalse()
    {
        var connector = CreateConnector(personalAccessToken: null);

        connector.IsAvailable.Should().BeFalse();
    }

    [Fact]
    public void SupportedOperations_ReturnsExpectedSet()
    {
        var connector = CreateConnector();

        connector.SupportedOperations.Should().BeEquivalentTo(
            ["list_work_items", "create_work_item", "update_work_item", "create_sprint"]);
    }

    [Fact]
    public void ToolName_ReturnsExpectedValue()
    {
        var connector = CreateConnector();

        connector.ToolName.Should().Be("azure_devops_work_items");
    }

    #endregion

    #region ValidateParametersAsync

    [Fact]
    public async Task ValidateParametersAsync_ListWorkItems_MissingProjectAndNoDefault_ReturnsError()
    {
        var connector = CreateConnector(defaultProject: null);
        var parameters = new Dictionary<string, object>();

        var errors = await connector.ValidateParametersAsync("list_work_items", parameters);

        errors.Should().Contain(e => e.Contains("Project"));
    }

    [Fact]
    public async Task ValidateParametersAsync_ListWorkItems_DefaultProjectUsed_ReturnsNoErrors()
    {
        var connector = CreateConnector(defaultProject: "MyProject");
        var parameters = new Dictionary<string, object>();

        var errors = await connector.ValidateParametersAsync("list_work_items", parameters);

        errors.Should().BeEmpty();
    }

    [Fact]
    public async Task ValidateParametersAsync_CreateWorkItem_MissingTypeAndTitle_ReturnsErrors()
    {
        var connector = CreateConnector();
        var parameters = new Dictionary<string, object>();

        var errors = await connector.ValidateParametersAsync("create_work_item", parameters);

        errors.Should().Contain(e => e.Contains("type"));
        errors.Should().Contain(e => e.Contains("Title"));
    }

    [Fact]
    public async Task ValidateParametersAsync_CreateWorkItem_AllParamsPresent_ReturnsNoErrors()
    {
        var connector = CreateConnector();
        var parameters = new Dictionary<string, object>
        {
            ["type"] = "Bug",
            ["title"] = "Fix login issue"
        };

        var errors = await connector.ValidateParametersAsync("create_work_item", parameters);

        errors.Should().BeEmpty();
    }

    [Fact]
    public async Task ValidateParametersAsync_UpdateWorkItem_MissingId_ReturnsError()
    {
        var connector = CreateConnector();
        var parameters = new Dictionary<string, object> { ["title"] = "Updated" };

        var errors = await connector.ValidateParametersAsync("update_work_item", parameters);

        errors.Should().Contain(e => e.Contains("Work item ID"));
    }

    [Fact]
    public async Task ValidateParametersAsync_UpdateWorkItem_MissingUpdatesAndTitle_ReturnsError()
    {
        var connector = CreateConnector();
        var parameters = new Dictionary<string, object> { ["id"] = 123 };

        var errors = await connector.ValidateParametersAsync("update_work_item", parameters);

        errors.Should().Contain(e => e.Contains("updates"));
    }

    [Fact]
    public async Task ValidateParametersAsync_UpdateWorkItem_WithTitle_ReturnsNoErrors()
    {
        var connector = CreateConnector();
        var parameters = new Dictionary<string, object>
        {
            ["id"] = 123,
            ["title"] = "Updated title"
        };

        var errors = await connector.ValidateParametersAsync("update_work_item", parameters);

        errors.Should().BeEmpty();
    }

    [Fact]
    public async Task ValidateParametersAsync_CreateSprint_MissingNameAndPath_ReturnsErrors()
    {
        var connector = CreateConnector();
        var parameters = new Dictionary<string, object>();

        var errors = await connector.ValidateParametersAsync("create_sprint", parameters);

        errors.Should().Contain(e => e.Contains("Sprint name"));
        errors.Should().Contain(e => e.Contains("Iteration path"));
    }

    [Fact]
    public async Task ValidateParametersAsync_CreateSprint_AllParamsPresent_ReturnsNoErrors()
    {
        var connector = CreateConnector();
        var parameters = new Dictionary<string, object>
        {
            ["name"] = "Sprint 1",
            ["iterationPath"] = @"TestProject\Sprint 1"
        };

        var errors = await connector.ValidateParametersAsync("create_sprint", parameters);

        errors.Should().BeEmpty();
    }

    #endregion

    #region ListWorkItems

    [Fact]
    public async Task ListWorkItems_ValidParams_ReturnsSuccessWithWorkItems()
    {
        var connector = CreateConnector();

        var wiqlResponse = JsonSerializer.Serialize(new
        {
            workItems = new[]
            {
                new { id = 1 },
                new { id = 2 }
            }
        });

        var detailsResponse = JsonSerializer.Serialize(new
        {
            value = new[]
            {
                new
                {
                    id = 1,
                    fields = new Dictionary<string, object>
                    {
                        ["System.Title"] = "First work item",
                        ["System.WorkItemType"] = "Bug",
                        ["System.State"] = "Active"
                    },
                    _links = new { html = new { href = "https://dev.azure.com/test-org/TestProject/_workitems/edit/1" } }
                },
                new
                {
                    id = 2,
                    fields = new Dictionary<string, object>
                    {
                        ["System.Title"] = "Second work item",
                        ["System.WorkItemType"] = "Task",
                        ["System.State"] = "New"
                    },
                    _links = new { html = new { href = "https://dev.azure.com/test-org/TestProject/_workitems/edit/2" } }
                }
            }
        });

        SetupHttpSequence(
            (HttpStatusCode.OK, wiqlResponse),
            (HttpStatusCode.OK, detailsResponse));

        var parameters = new Dictionary<string, object>();
        var result = await connector.ExecuteAsync("list_work_items", parameters);

        result.IsSuccess.Should().BeTrue();
        result.MarkdownResult.Should().Contain("First work item");
    }

    [Fact]
    public async Task ListWorkItems_EmptyResult_ReturnsSuccessWithNoItems()
    {
        var connector = CreateConnector();
        var wiqlResponse = JsonSerializer.Serialize(new { workItems = Array.Empty<object>() });
        SetupHttpResponse(HttpStatusCode.OK, wiqlResponse);

        var parameters = new Dictionary<string, object>();
        var result = await connector.ExecuteAsync("list_work_items", parameters);

        result.IsSuccess.Should().BeTrue();
        result.MarkdownResult.Should().Contain("No work items found");
    }

    [Fact]
    public async Task ListWorkItems_InvalidProjectName_ReturnsFailure()
    {
        var connector = CreateConnector(defaultProject: null);
        var parameters = new Dictionary<string, object>
        {
            ["project"] = "invalid<>project!@#$%^&*()"
        };
        var result = await connector.ExecuteAsync("list_work_items", parameters);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Invalid project name");
    }

    [Fact]
    public async Task ListWorkItems_HttpError_ReturnsFailure()
    {
        var connector = CreateConnector();
        SetupHttpResponse(HttpStatusCode.Unauthorized, "Authentication required");

        var parameters = new Dictionary<string, object>();
        var result = await connector.ExecuteAsync("list_work_items", parameters);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Unauthorized");
    }

    [Fact]
    public async Task ListWorkItems_NetworkError_ReturnsFailure()
    {
        var connector = CreateConnector();
        SetupHttpException(new HttpRequestException("Connection refused"));

        var parameters = new Dictionary<string, object>();
        var result = await connector.ExecuteAsync("list_work_items", parameters);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Network error");
    }

    [Fact]
    public async Task ListWorkItems_InvalidJson_ReturnsFailure()
    {
        var connector = CreateConnector();
        SetupHttpResponse(HttpStatusCode.OK, "not valid json");

        var parameters = new Dictionary<string, object>();
        var result = await connector.ExecuteAsync("list_work_items", parameters);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("parsing error");
    }

    #endregion

    #region CreateWorkItem

    [Fact]
    public async Task CreateWorkItem_ValidParams_ReturnsSuccessWithWorkItemDetails()
    {
        var connector = CreateConnector();
        var jsonResponse = JsonSerializer.Serialize(new
        {
            id = 100,
            rev = 1,
            fields = new Dictionary<string, object>
            {
                ["System.Title"] = "New work item",
                ["System.WorkItemType"] = "Bug"
            },
            _links = new { html = new { href = "https://dev.azure.com/test-org/TestProject/_workitems/edit/100" } }
        });
        SetupHttpResponse(HttpStatusCode.OK, jsonResponse);

        var parameters = new Dictionary<string, object>
        {
            ["type"] = "Bug",
            ["title"] = "New work item"
        };
        var result = await connector.ExecuteAsync("create_work_item", parameters);

        result.IsSuccess.Should().BeTrue();
        result.MarkdownResult.Should().Contain("Work Item Created");
        result.MarkdownResult.Should().Contain("100");
    }

    [Fact]
    public async Task CreateWorkItem_HttpError_ReturnsFailure()
    {
        var connector = CreateConnector();
        SetupHttpResponse(HttpStatusCode.BadRequest, "Invalid work item type");

        var parameters = new Dictionary<string, object>
        {
            ["type"] = "InvalidType",
            ["title"] = "Test"
        };
        var result = await connector.ExecuteAsync("create_work_item", parameters);

        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task CreateWorkItem_NetworkError_ReturnsFailure()
    {
        var connector = CreateConnector();
        SetupHttpException(new HttpRequestException("DNS resolution failed"));

        var parameters = new Dictionary<string, object>
        {
            ["type"] = "Bug",
            ["title"] = "Test"
        };
        var result = await connector.ExecuteAsync("create_work_item", parameters);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Network error");
    }

    [Fact]
    public async Task CreateWorkItem_InvalidJsonResponse_ReturnsFailure()
    {
        var connector = CreateConnector();
        SetupHttpResponse(HttpStatusCode.OK, "not json");

        var parameters = new Dictionary<string, object>
        {
            ["type"] = "Bug",
            ["title"] = "Test"
        };
        var result = await connector.ExecuteAsync("create_work_item", parameters);

        result.IsSuccess.Should().BeFalse();
    }

    #endregion

    #region UpdateWorkItem

    [Fact]
    public async Task UpdateWorkItem_ValidParams_ReturnsSuccess()
    {
        var connector = CreateConnector();
        var jsonResponse = JsonSerializer.Serialize(new
        {
            id = 50,
            rev = 3,
            _links = new { html = new { href = "https://dev.azure.com/test-org/TestProject/_workitems/edit/50" } }
        });
        SetupHttpResponse(HttpStatusCode.OK, jsonResponse);

        var parameters = new Dictionary<string, object>
        {
            ["id"] = 50,
            ["title"] = "Updated work item title"
        };
        var result = await connector.ExecuteAsync("update_work_item", parameters);

        result.IsSuccess.Should().BeTrue();
        result.MarkdownResult.Should().Contain("Updated");
    }

    [Fact]
    public async Task UpdateWorkItem_HttpError_ReturnsFailure()
    {
        var connector = CreateConnector();
        SetupHttpResponse(HttpStatusCode.NotFound, "Work item not found");

        var parameters = new Dictionary<string, object>
        {
            ["id"] = 999,
            ["title"] = "Updated"
        };
        var result = await connector.ExecuteAsync("update_work_item", parameters);

        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task UpdateWorkItem_NetworkError_ReturnsFailure()
    {
        var connector = CreateConnector();
        SetupHttpException(new HttpRequestException("Timeout"));

        var parameters = new Dictionary<string, object>
        {
            ["id"] = 50,
            ["title"] = "Updated"
        };
        var result = await connector.ExecuteAsync("update_work_item", parameters);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Network error");
    }

    #endregion

    #region CreateSprint

    [Fact]
    public async Task CreateSprint_ValidParams_ReturnsSuccess()
    {
        var connector = CreateConnector();
        SetupHttpResponse(HttpStatusCode.OK, "{}");

        var parameters = new Dictionary<string, object>
        {
            ["name"] = "Sprint 1",
            ["iterationPath"] = @"TestProject\Sprint 1"
        };
        var result = await connector.ExecuteAsync("create_sprint", parameters);

        result.IsSuccess.Should().BeTrue();
        result.MarkdownResult.Should().Contain("Sprint Created");
        result.MarkdownResult.Should().Contain("Sprint 1");
    }

    [Fact]
    public async Task CreateSprint_HttpError_ReturnsFailure()
    {
        var connector = CreateConnector();
        SetupHttpResponse(HttpStatusCode.Conflict, "Sprint already exists");

        var parameters = new Dictionary<string, object>
        {
            ["name"] = "Sprint 1",
            ["iterationPath"] = @"TestProject\Sprint 1"
        };
        var result = await connector.ExecuteAsync("create_sprint", parameters);

        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task CreateSprint_NetworkError_ReturnsFailure()
    {
        var connector = CreateConnector();
        SetupHttpException(new HttpRequestException("Connection refused"));

        var parameters = new Dictionary<string, object>
        {
            ["name"] = "Sprint 1",
            ["iterationPath"] = @"TestProject\Sprint 1"
        };
        var result = await connector.ExecuteAsync("create_sprint", parameters);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Network error");
    }

    #endregion

    #region Unavailable Connector

    [Fact]
    public async Task ExecuteAsync_WhenUnavailable_ReturnsFailure()
    {
        var connector = CreateConnector(personalAccessToken: null);

        var result = await connector.ExecuteAsync("list_work_items", new Dictionary<string, object>());

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("not configured");
    }

    [Fact]
    public async Task ExecuteAsync_UnsupportedOperation_ReturnsFailure()
    {
        var connector = CreateConnector();

        var result = await connector.ExecuteAsync("delete_work_item", new Dictionary<string, object>());

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("not supported");
    }

    #endregion
}
