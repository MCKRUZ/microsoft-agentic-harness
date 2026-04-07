using Application.AI.Common.Interfaces.Connectors;
using Domain.Common.Config;
using Domain.Common.Config.Connectors;
using Infrastructure.AI.Connectors.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text;
using System.Text.Json;

namespace Infrastructure.AI.Connectors.GitHub;

/// <summary>
/// Connector client for GitHub issues operations.
/// Supports listing, creating, updating, and closing issues
/// via the GitHub REST API v3.
/// </summary>
public sealed class GitHubIssuesConnector : ConnectorClientBase
{
    #region Variables

    private GitHubConfig Config => _appConfig.CurrentValue.Connectors.GitHub;

    private readonly string[] _supportedOperations =
    [
        "list_issues",
        "create_issue",
        "update_issue",
        "close_issue"
    ];

    #endregion

    #region Constructor

    /// <summary>
    /// Initializes a new instance of <see cref="GitHubIssuesConnector"/>.
    /// </summary>
    public GitHubIssuesConnector(
        ILogger<GitHubIssuesConnector> logger,
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<AppConfig> appConfig)
        : base(logger, httpClientFactory, appConfig)
    {
    }

    #endregion

    #region IConnectorClient Implementation

    /// <inheritdoc/>
    public override string ToolName => "github_issues";

    /// <inheritdoc/>
    public override bool IsAvailable => Config.IsConfigured;

    /// <inheritdoc/>
    public override IReadOnlyList<string> SupportedOperations => _supportedOperations;

    /// <inheritdoc/>
    public override Task<List<string>> ValidateParametersAsync(
        string operation,
        Dictionary<string, object> parameters)
    {
        var errors = new List<string>();

        if (!parameters.ContainsKey("owner") && string.IsNullOrWhiteSpace(Config.DefaultOwner))
            errors.Add("Owner is required when DefaultOwner is not configured");
        if (!parameters.ContainsKey("repo"))
            errors.Add("Repository name is required");

        switch (operation)
        {
            case "create_issue":
                if (!parameters.ContainsKey("title"))
                    errors.Add("Issue title is required");
                break;
            case "update_issue":
            case "close_issue":
                if (!parameters.ContainsKey("issueNumber"))
                    errors.Add("Issue number is required");
                break;
        }

        return Task.FromResult(errors);
    }

    #endregion

    #region ExecuteOperationAsync

    /// <inheritdoc/>
    protected override async Task<ConnectorOperationResult> ExecuteOperationAsync(
        string operation,
        Dictionary<string, object> parameters,
        CancellationToken cancellationToken)
    {
        return operation switch
        {
            "list_issues" => await ListIssuesAsync(parameters, cancellationToken),
            "create_issue" => await CreateIssueAsync(parameters, cancellationToken),
            "update_issue" => await UpdateIssueAsync(parameters, cancellationToken),
            "close_issue" => await CloseIssueAsync(parameters, cancellationToken),
            _ => ConnectorOperationResult.Failure($"Operation '{operation}' is not implemented")
        };
    }

    #endregion

    #region Operations

    private async Task<ConnectorOperationResult> ListIssuesAsync(
        Dictionary<string, object> parameters,
        CancellationToken cancellationToken)
    {
        var config = Config;
        var owner = Uri.EscapeDataString(GetOptionalParameter<string>(parameters, "owner") ?? config.DefaultOwner!);
        var repo = Uri.EscapeDataString(GetRequiredParameter<string>(parameters, "repo"));
        var state = GetOptionalParameter<string>(parameters, "state", "open") ?? "open";
        var perPage = GetOptionalParameter<int>(parameters, "perPage", 30);
        var page = GetOptionalParameter<int>(parameters, "page", 1);
        var labels = GetOptionalParameter<string>(parameters, "labels");

        try
        {
            var httpClient = CreateHttpClient();
            ConfigureHttpClient(httpClient);

            var queryParams = new List<string>
            {
                $"state={state}",
                $"per_page={perPage}",
                $"page={page}"
            };
            if (!string.IsNullOrWhiteSpace(labels))
                queryParams.Add($"labels={Uri.EscapeDataString(labels)}");

            var url = $"{config.BaseUrl}/repos/{owner}/{repo}/issues?{string.Join("&", queryParams)}";
            var response = await httpClient.GetAsync(url, cancellationToken);

            var httpError = await CheckHttpErrorAsync(response, "List issues", cancellationToken);
            if (httpError != null) return httpError;

            var result = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(result);

            var issues = ParseIssuesFromJson(doc);
            var markdown = BuildIssuesMarkdown(issues, owner, repo, state);
            _logger.LogInformation("Listed {Count} issues from {Owner}/{Repo}", issues.Count, owner, repo);

            return ConnectorOperationResult.Success(issues, markdown);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error listing issues");
            return ConnectorOperationResult.Failure($"Network error: {ex.Message}");
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "JSON parsing error listing issues");
            return ConnectorOperationResult.Failure($"Response parsing error: {ex.Message}");
        }
    }

    private async Task<ConnectorOperationResult> CreateIssueAsync(
        Dictionary<string, object> parameters,
        CancellationToken cancellationToken)
    {
        var config = Config;
        var owner = Uri.EscapeDataString(GetOptionalParameter<string>(parameters, "owner") ?? config.DefaultOwner!);
        var repo = Uri.EscapeDataString(GetRequiredParameter<string>(parameters, "repo"));
        var title = GetRequiredParameter<string>(parameters, "title");
        var body = GetOptionalParameter<string>(parameters, "body");
        var labels = GetOptionalParameter<List<string>>(parameters, "labels");

        try
        {
            var httpClient = CreateHttpClient();
            ConfigureHttpClient(httpClient);

            var payload = new Dictionary<string, object?> { ["title"] = title };
            if (!string.IsNullOrWhiteSpace(body)) payload["body"] = body;
            if (labels != null && labels.Count > 0) payload["labels"] = labels;

            var url = $"{config.BaseUrl}/repos/{owner}/{repo}/issues";
            var response = await httpClient.PostAsync(url, CreateJsonContent(payload), cancellationToken);

            var httpError = await CheckHttpErrorAsync(response, "Create issue", cancellationToken);
            if (httpError != null) return httpError;

            var result = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(result);

            var issueNumber = doc.RootElement.GetProperty("number").GetInt32();
            var issueTitle = doc.RootElement.GetProperty("title").GetString();
            var htmlUrl = doc.RootElement.GetProperty("html_url").GetString();
            var state = doc.RootElement.GetProperty("state").GetString();

            var markdown = $"## Issue Created\n\n**#{issueNumber}:** {issueTitle}\n" +
                $"**Repository:** {owner}/{repo}\n**State:** {state}\n**URL:** {htmlUrl}\n";

            _logger.LogInformation("Created issue #{IssueNumber} in {Owner}/{Repo}", issueNumber, owner, repo);
            return ConnectorOperationResult.Success(
                new { number = issueNumber, title = issueTitle, repository = $"{owner}/{repo}", state, url = htmlUrl }, markdown);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error creating issue");
            return ConnectorOperationResult.Failure($"Network error: {ex.Message}");
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException)
        {
            _logger.LogError(ex, "Error creating issue");
            return ConnectorOperationResult.Failure($"Error: {ex.Message}");
        }
    }

    private async Task<ConnectorOperationResult> UpdateIssueAsync(
        Dictionary<string, object> parameters,
        CancellationToken cancellationToken)
    {
        var config = Config;
        var owner = Uri.EscapeDataString(GetOptionalParameter<string>(parameters, "owner") ?? config.DefaultOwner!);
        var repo = Uri.EscapeDataString(GetRequiredParameter<string>(parameters, "repo"));
        var issueNumber = GetRequiredParameter<int>(parameters, "issueNumber");
        var title = GetOptionalParameter<string>(parameters, "title");
        var body = GetOptionalParameter<string>(parameters, "body");
        var state = GetOptionalParameter<string>(parameters, "state");
        var labels = GetOptionalParameter<List<string>>(parameters, "labels");

        try
        {
            var httpClient = CreateHttpClient();
            ConfigureHttpClient(httpClient);

            var payload = new Dictionary<string, object?>();
            if (!string.IsNullOrWhiteSpace(title)) payload["title"] = title;
            if (!string.IsNullOrWhiteSpace(body)) payload["body"] = body;
            if (!string.IsNullOrWhiteSpace(state)) payload["state"] = state;
            if (labels != null) payload["labels"] = labels;

            if (payload.Count == 0)
                return ConnectorOperationResult.Failure("At least one field must be provided to update");

            var url = $"{config.BaseUrl}/repos/{owner}/{repo}/issues/{issueNumber}";
            var response = await httpClient.PatchAsync(new Uri(url), CreateJsonContent(payload), cancellationToken);

            var httpError = await CheckHttpErrorAsync(response, "Update issue", cancellationToken);
            if (httpError != null) return httpError;

            var result = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(result);

            var updatedTitle = doc.RootElement.GetProperty("title").GetString();
            var htmlUrl = doc.RootElement.GetProperty("html_url").GetString();
            var updatedState = doc.RootElement.GetProperty("state").GetString();

            var markdown = $"## Issue Updated\n\n**#{issueNumber}:** {updatedTitle}\n" +
                $"**Repository:** {owner}/{repo}\n**State:** {updatedState}\n**URL:** {htmlUrl}\n";

            _logger.LogInformation("Updated issue #{IssueNumber} in {Owner}/{Repo}", issueNumber, owner, repo);
            return ConnectorOperationResult.Success(
                new { number = issueNumber, title = updatedTitle, repository = $"{owner}/{repo}", state = updatedState, url = htmlUrl }, markdown);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error updating issue");
            return ConnectorOperationResult.Failure($"Network error: {ex.Message}");
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException)
        {
            _logger.LogError(ex, "Error updating issue");
            return ConnectorOperationResult.Failure($"Error: {ex.Message}");
        }
    }

    private async Task<ConnectorOperationResult> CloseIssueAsync(
        Dictionary<string, object> parameters,
        CancellationToken cancellationToken)
    {
        var config = Config;
        var owner = Uri.EscapeDataString(GetOptionalParameter<string>(parameters, "owner") ?? config.DefaultOwner!);
        var repo = Uri.EscapeDataString(GetRequiredParameter<string>(parameters, "repo"));
        var issueNumber = GetRequiredParameter<int>(parameters, "issueNumber");
        var comment = GetOptionalParameter<string>(parameters, "comment");
        var stateReason = GetOptionalParameter<string>(parameters, "stateReason", "completed");

        try
        {
            var httpClient = CreateHttpClient();
            ConfigureHttpClient(httpClient);

            if (!string.IsNullOrWhiteSpace(comment))
            {
                var commentUrl = $"{config.BaseUrl}/repos/{owner}/{repo}/issues/{issueNumber}/comments";
                var commentResponse = await httpClient.PostAsync(commentUrl, CreateJsonContent(new { body = comment }), cancellationToken);
                if (!commentResponse.IsSuccessStatusCode)
                    _logger.LogWarning("Failed to add closing comment to issue #{IssueNumber}", issueNumber);
            }

            var payload = new { state = "closed", state_reason = stateReason };
            var url = $"{config.BaseUrl}/repos/{owner}/{repo}/issues/{issueNumber}";
            var response = await httpClient.PatchAsync(new Uri(url), CreateJsonContent(payload), cancellationToken);

            var httpError = await CheckHttpErrorAsync(response, "Close issue", cancellationToken);
            if (httpError != null) return httpError;

            var result = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(result);

            var title = doc.RootElement.GetProperty("title").GetString();
            var htmlUrl = doc.RootElement.GetProperty("html_url").GetString();

            var markdown = $"## Issue Closed\n\n**#{issueNumber}:** {title}\n" +
                $"**Repository:** {owner}/{repo}\n**URL:** {htmlUrl}\n";

            _logger.LogInformation("Closed issue #{IssueNumber} in {Owner}/{Repo}", issueNumber, owner, repo);
            return ConnectorOperationResult.Success(
                new { number = issueNumber, title, repository = $"{owner}/{repo}", url = htmlUrl }, markdown);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error closing issue");
            return ConnectorOperationResult.Failure($"Network error: {ex.Message}");
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException)
        {
            _logger.LogError(ex, "Error closing issue");
            return ConnectorOperationResult.Failure($"Error: {ex.Message}");
        }
    }

    #endregion

    #region Helper Methods

    private void ConfigureHttpClient(HttpClient httpClient)
    {
        var config = Config;
        ConfigureHttpClientBase(httpClient, config.TimeoutSeconds);
        AddAcceptHeader(httpClient, "application/vnd.github.v3+json");
        AddUserAgentHeader(httpClient, "Agentic-Harness-Connector");
        AddBearerAuth(httpClient, config.AccessToken!);
    }

    private static List<IssueInfo> ParseIssuesFromJson(JsonDocument doc)
    {
        var issues = new List<IssueInfo>();
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return issues;

        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var labels = new List<string>();
            if (item.TryGetProperty("labels", out var labelsProp) && labelsProp.ValueKind == JsonValueKind.Array)
            {
                labels = labelsProp.EnumerateArray()
                    .Where(l => l.TryGetProperty("name", out _))
                    .Select(l => l.GetProperty("name").GetString() ?? string.Empty)
                    .ToList();
            }

            issues.Add(new IssueInfo
            {
                Number = item.GetProperty("number").GetInt32(),
                Title = item.GetProperty("title").GetString() ?? string.Empty,
                State = item.TryGetProperty("state", out var stateProp) ? stateProp.GetString() ?? "open" : "open",
                Author = item.TryGetProperty("user", out var userProp) && userProp.TryGetProperty("login", out var loginProp)
                    ? loginProp.GetString() : null,
                Labels = labels,
                HtmlUrl = item.TryGetProperty("html_url", out var urlProp) ? urlProp.GetString() : null,
                CreatedAt = item.TryGetProperty("created_at", out var createdProp) ? createdProp.GetDateTime() : default
            });
        }

        return issues;
    }

    private static string BuildIssuesMarkdown(List<IssueInfo> issues, string owner, string repo, string state)
    {
        if (issues.Count == 0)
            return $"## Issues\n\nNo {state} issues found in {owner}/{repo}.";

        var sb = new StringBuilder();
        sb.AppendLine($"## Issues in {owner}/{repo} ({issues.Count})\n");

        foreach (var issue in issues)
        {
            sb.AppendLine($"### #{issue.Number} {issue.Title}");
            sb.AppendLine($"- **State:** {issue.State}");
            if (!string.IsNullOrWhiteSpace(issue.Author))
                sb.AppendLine($"- **Author:** {issue.Author}");
            if (issue.Labels.Count > 0)
                sb.AppendLine($"- **Labels:** {string.Join(", ", issue.Labels)}");
            if (!string.IsNullOrWhiteSpace(issue.HtmlUrl))
                sb.AppendLine($"- **URL:** {issue.HtmlUrl}");
            if (issue.CreatedAt != default)
                sb.AppendLine($"- **Created:** {issue.CreatedAt:yyyy-MM-dd}");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    #endregion

    #region Nested Types

    private sealed class IssueInfo
    {
        public int Number { get; init; }
        public required string Title { get; init; }
        public string State { get; init; } = "open";
        public string? Author { get; init; }
        public IReadOnlyList<string> Labels { get; init; } = [];
        public string? HtmlUrl { get; init; }
        public DateTime CreatedAt { get; init; }
    }

    #endregion
}
