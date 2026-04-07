using Application.AI.Common.Interfaces.Connectors;
using Domain.Common.Config;
using Domain.Common.Config.Connectors;
using Infrastructure.AI.Connectors.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text;
using System.Text.Json;

namespace Infrastructure.AI.Connectors.Jira;

/// <summary>
/// Connector client for Jira issues operations.
/// Supports listing, creating, updating, and transitioning issues
/// using Jira REST API v3 with Atlassian Document Format (ADF).
/// </summary>
public sealed class JiraIssuesConnector : ConnectorClientBase
{
    #region Variables

    private JiraConfig Config => _appConfig.CurrentValue.Connectors.Jira;

    private readonly string[] _supportedOperations =
    [
        "list_issues",
        "create_issue",
        "update_issue",
        "transition_issue"
    ];

    #endregion

    #region Constructor

    /// <summary>
    /// Initializes a new instance of <see cref="JiraIssuesConnector"/>.
    /// </summary>
    public JiraIssuesConnector(
        ILogger<JiraIssuesConnector> logger,
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<AppConfig> appConfig)
        : base(logger, httpClientFactory, appConfig)
    {
    }

    #endregion

    #region IConnectorClient Implementation

    /// <inheritdoc/>
    public override string ToolName => "jira_issues";

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

        switch (operation)
        {
            case "list_issues":
                if (!parameters.ContainsKey("project") && string.IsNullOrWhiteSpace(Config.DefaultProject))
                    errors.Add("Project key is required when DefaultProject is not configured");
                break;
            case "create_issue":
                if (!parameters.ContainsKey("project") && string.IsNullOrWhiteSpace(Config.DefaultProject))
                    errors.Add("Project key is required when DefaultProject is not configured");
                if (!parameters.ContainsKey("summary"))
                    errors.Add("Issue summary is required");
                if (!parameters.ContainsKey("issueType"))
                    errors.Add("Issue type is required (e.g., 'Bug', 'Task', 'Story')");
                break;
            case "update_issue":
                if (!parameters.ContainsKey("issueIdOrKey"))
                    errors.Add("Issue ID or Key is required");
                break;
            case "transition_issue":
                if (!parameters.ContainsKey("issueIdOrKey"))
                    errors.Add("Issue ID or Key is required");
                if (!parameters.ContainsKey("transition"))
                    errors.Add("Transition name or ID is required");
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
            "transition_issue" => await TransitionIssueAsync(parameters, cancellationToken),
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
        var project = GetOptionalParameter<string>(parameters, "project") ?? config.DefaultProject!;
        var jql = GetOptionalParameter<string>(parameters, "jql");
        var startAt = GetOptionalParameter<int>(parameters, "startAt", 0);
        var maxResults = GetOptionalParameter<int>(parameters, "maxResults", 50);

        try
        {
            var httpClient = CreateHttpClient();
            ConfigureHttpClient(httpClient);

            var query = string.IsNullOrWhiteSpace(jql)
                ? $"project = \"{project}\" ORDER BY updated DESC"
                : jql;

            var searchPayload = new
            {
                jql = query, startAt, maxResults,
                fields = new[] { "summary", "status", "assignee", "reporter", "priority", "issuetype", "created", "updated" }
            };

            var baseUrl = config.BaseUrl!.TrimEnd('/');
            var url = $"{baseUrl}/rest/api/3/search";
            var response = await httpClient.PostAsync(url, CreateJsonContent(searchPayload), cancellationToken);

            var httpError = await CheckHttpErrorAsync(response, "List issues", cancellationToken);
            if (httpError != null) return httpError;

            var result = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(result);

            var issues = ParseIssuesFromJson(doc, baseUrl);
            var total = doc.RootElement.TryGetProperty("total", out var totalProp) ? totalProp.GetInt32() : issues.Count;

            _logger.LogInformation("Listed {Count} issues from Jira project {Project}", issues.Count, project);
            return ConnectorOperationResult.Success(issues, BuildIssuesMarkdown(issues, project, total, startAt));
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error listing Jira issues");
            return ConnectorOperationResult.Failure($"Network error: {ex.Message}");
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "JSON parsing error listing Jira issues");
            return ConnectorOperationResult.Failure($"Response parsing error: {ex.Message}");
        }
    }

    private async Task<ConnectorOperationResult> CreateIssueAsync(
        Dictionary<string, object> parameters,
        CancellationToken cancellationToken)
    {
        var config = Config;
        var project = GetOptionalParameter<string>(parameters, "project") ?? config.DefaultProject!;
        var summary = GetRequiredParameter<string>(parameters, "summary");
        var issueType = GetRequiredParameter<string>(parameters, "issueType");
        var description = GetOptionalParameter<string>(parameters, "description");
        var priority = GetOptionalParameter<string>(parameters, "priority");

        try
        {
            var httpClient = CreateHttpClient();
            ConfigureHttpClient(httpClient);

            var fields = new Dictionary<string, object?>
            {
                ["project"] = new { key = project },
                ["summary"] = summary,
                ["issuetype"] = new { name = issueType }
            };

            if (!string.IsNullOrWhiteSpace(description))
                fields["description"] = BuildAdfParagraph(description);
            if (!string.IsNullOrWhiteSpace(priority))
                fields["priority"] = new { name = priority };

            var payloadJson = $"{{\"fields\":{JsonSerializer.Serialize(fields, JsonOptions)}}}";
            var baseUrl = config.BaseUrl!.TrimEnd('/');
            var url = $"{baseUrl}/rest/api/3/issue";
            var response = await httpClient.PostAsync(url, CreateJsonContent(payloadJson), cancellationToken);

            var httpError = await CheckHttpErrorAsync(response, "Create issue", cancellationToken);
            if (httpError != null) return httpError;

            var result = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(result);

            var issueKey = doc.RootElement.GetProperty("key").GetString();
            var browseUrl = $"{baseUrl}/browse/{issueKey}";

            var markdown = $"## Jira Issue Created\n\n**Key:** {issueKey}\n**Summary:** {summary}\n" +
                $"**Type:** {issueType}\n**Project:** {project}\n**URL:** {browseUrl}\n";

            _logger.LogInformation("Created Jira issue {IssueKey} in project {Project}", issueKey, project);
            return ConnectorOperationResult.Success(
                new { key = issueKey, summary, type = issueType, project, url = browseUrl }, markdown);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error creating Jira issue");
            return ConnectorOperationResult.Failure($"Network error: {ex.Message}");
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException)
        {
            _logger.LogError(ex, "Error creating Jira issue");
            return ConnectorOperationResult.Failure($"Error: {ex.Message}");
        }
    }

    private async Task<ConnectorOperationResult> UpdateIssueAsync(
        Dictionary<string, object> parameters,
        CancellationToken cancellationToken)
    {
        var config = Config;
        var issueIdOrKey = Uri.EscapeDataString(GetRequiredParameter<string>(parameters, "issueIdOrKey"));
        var summary = GetOptionalParameter<string>(parameters, "summary");
        var description = GetOptionalParameter<string>(parameters, "description");
        var priority = GetOptionalParameter<string>(parameters, "priority");

        try
        {
            var httpClient = CreateHttpClient();
            ConfigureHttpClient(httpClient);

            var fields = new Dictionary<string, object?>();
            if (!string.IsNullOrWhiteSpace(summary)) fields["summary"] = summary;
            if (!string.IsNullOrWhiteSpace(description)) fields["description"] = BuildAdfParagraph(description);
            if (!string.IsNullOrWhiteSpace(priority)) fields["priority"] = new { name = priority };

            if (fields.Count == 0)
                return ConnectorOperationResult.Failure("At least one field must be provided to update");

            var payloadJson = $"{{\"fields\":{JsonSerializer.Serialize(fields, JsonOptions)}}}";
            var baseUrl = config.BaseUrl!.TrimEnd('/');
            var url = $"{baseUrl}/rest/api/3/issue/{issueIdOrKey}";
            var response = await httpClient.PutAsync(new Uri(url), CreateJsonContent(payloadJson), cancellationToken);

            var httpError = await CheckHttpErrorAsync(response, "Update issue", cancellationToken);
            if (httpError != null) return httpError;

            var browseUrl = $"{baseUrl}/browse/{issueIdOrKey}";
            _logger.LogInformation("Updated Jira issue {IssueKey}", issueIdOrKey);
            return ConnectorOperationResult.Success(
                new { key = issueIdOrKey, url = browseUrl },
                $"## Jira Issue Updated\n\n**Key:** {issueIdOrKey}\n**URL:** {browseUrl}\n");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error updating Jira issue");
            return ConnectorOperationResult.Failure($"Network error: {ex.Message}");
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException)
        {
            _logger.LogError(ex, "Error updating Jira issue");
            return ConnectorOperationResult.Failure($"Error: {ex.Message}");
        }
    }

    private async Task<ConnectorOperationResult> TransitionIssueAsync(
        Dictionary<string, object> parameters,
        CancellationToken cancellationToken)
    {
        var config = Config;
        var issueIdOrKey = Uri.EscapeDataString(GetRequiredParameter<string>(parameters, "issueIdOrKey"));
        var transition = GetRequiredParameter<object>(parameters, "transition");
        var comment = GetOptionalParameter<string>(parameters, "comment");

        try
        {
            var httpClient = CreateHttpClient();
            ConfigureHttpClient(httpClient);

            var transitionObj = BuildTransitionObject(transition);
            var payload = new Dictionary<string, object?> { ["transition"] = transitionObj };

            if (!string.IsNullOrWhiteSpace(comment))
                payload["update"] = BuildCommentUpdate(comment);

            var baseUrl = config.BaseUrl!.TrimEnd('/');
            var url = $"{baseUrl}/rest/api/3/issue/{issueIdOrKey}/transitions";
            var response = await httpClient.PostAsync(url, CreateJsonContent(payload), cancellationToken);

            var httpError = await CheckHttpErrorAsync(response, "Transition issue", cancellationToken);
            if (httpError != null) return httpError;

            var browseUrl = $"{baseUrl}/browse/{issueIdOrKey}";
            var transitionName = transitionObj.TryGetValue("name", out var nameValue)
                ? nameValue?.ToString() : $"ID {transitionObj.GetValueOrDefault("id")}";

            _logger.LogInformation("Transitioned Jira issue {IssueKey} to {Transition}", issueIdOrKey, transitionName);
            return ConnectorOperationResult.Success(
                new { key = issueIdOrKey, transition = transitionName, url = browseUrl },
                $"## Jira Issue Transitioned\n\n**Key:** {issueIdOrKey}\n**Transition:** {transitionName}\n**URL:** {browseUrl}\n");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error transitioning Jira issue");
            return ConnectorOperationResult.Failure($"Network error: {ex.Message}");
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException)
        {
            _logger.LogError(ex, "Error transitioning Jira issue");
            return ConnectorOperationResult.Failure($"Error: {ex.Message}");
        }
    }

    #endregion

    #region Helper Methods

    private void ConfigureHttpClient(HttpClient httpClient)
    {
        var config = Config;
        ConfigureHttpClientBase(httpClient, config.TimeoutSeconds);
        AddBasicAuth(httpClient, config.Email!, config.ApiToken!);
        AddAcceptHeader(httpClient);
    }

    private static object BuildAdfParagraph(string text) => new
    {
        version = 1, type = "doc",
        content = new[] { new { type = "paragraph", content = new[] { new { type = "text", text } } } }
    };

    private static Dictionary<string, object?> BuildTransitionObject(object transition)
    {
        var transitionStr = transition.ToString();
        return int.TryParse(transitionStr, out var id)
            ? new Dictionary<string, object?> { ["id"] = id }
            : new Dictionary<string, object?> { ["name"] = transitionStr ?? string.Empty };
    }

    private static object BuildCommentUpdate(string comment) => new
    {
        comment = new[]
        {
            new { add = new { body = BuildAdfParagraph(comment) } }
        }
    };

    private static List<JiraIssueInfo> ParseIssuesFromJson(JsonDocument doc, string baseUrl)
    {
        var issues = new List<JiraIssueInfo>();
        if (!doc.RootElement.TryGetProperty("issues", out var issuesArray) || issuesArray.ValueKind != JsonValueKind.Array)
            return issues;

        foreach (var issue in issuesArray.EnumerateArray())
        {
            var fields = issue.GetProperty("fields");
            var key = issue.GetProperty("key").GetString() ?? string.Empty;

            issues.Add(new JiraIssueInfo
            {
                Key = key,
                Summary = fields.TryGetProperty("summary", out var sp) ? sp.GetString() ?? string.Empty : string.Empty,
                Status = fields.TryGetProperty("status", out var st) && st.TryGetProperty("name", out var sn) ? sn.GetString() : null,
                Assignee = fields.TryGetProperty("assignee", out var ap) && ap.ValueKind == JsonValueKind.Object
                    && ap.TryGetProperty("displayName", out var dn) ? dn.GetString() : null,
                Priority = fields.TryGetProperty("priority", out var pp) && pp.TryGetProperty("name", out var pn) ? pn.GetString() : null,
                IssueType = fields.TryGetProperty("issuetype", out var tp) && tp.TryGetProperty("name", out var tn) ? tn.GetString() : null,
                Url = $"{baseUrl}/browse/{key}"
            });
        }
        return issues;
    }

    private static string BuildIssuesMarkdown(List<JiraIssueInfo> issues, string project, int total, int startAt)
    {
        if (issues.Count == 0)
            return $"## Jira Issues\n\nNo issues found in project {project}.";

        var sb = new StringBuilder();
        sb.AppendLine($"## Jira Issues in {project} (Showing {startAt + 1}-{startAt + issues.Count} of {total})\n");

        foreach (var issue in issues)
        {
            sb.AppendLine($"### [{issue.Key}] {issue.Summary}");
            sb.AppendLine($"- **Type:** {issue.IssueType ?? "N/A"} | **Status:** {issue.Status ?? "N/A"}");
            if (!string.IsNullOrWhiteSpace(issue.Assignee)) sb.AppendLine($"- **Assignee:** {issue.Assignee}");
            if (!string.IsNullOrWhiteSpace(issue.Priority)) sb.AppendLine($"- **Priority:** {issue.Priority}");
            if (!string.IsNullOrWhiteSpace(issue.Url)) sb.AppendLine($"- **URL:** {issue.Url}");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    #endregion

    #region Nested Types

    private sealed class JiraIssueInfo
    {
        public required string Key { get; init; }
        public required string Summary { get; init; }
        public string? Status { get; init; }
        public string? Assignee { get; init; }
        public string? Priority { get; init; }
        public string? IssueType { get; init; }
        public string? Url { get; init; }
    }

    #endregion
}
