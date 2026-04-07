using Application.AI.Common.Interfaces.Connectors;
using Ardalis.GuardClauses;
using Domain.Common.Config;
using Domain.Common.Config.Connectors;
using Infrastructure.AI.Connectors.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Infrastructure.AI.Connectors.AzureDevOps;

/// <summary>
/// Connector client for Azure DevOps Work Items operations.
/// Supports listing, creating, and updating work items, plus sprint management
/// via the Azure DevOps REST API.
/// </summary>
/// <remarks>
/// Authentication uses Basic auth with an empty username and a Personal Access Token.
/// Requires PAT scopes: Work Items (Read &amp; Write), Build (Read &amp; Execute).
/// </remarks>
public sealed partial class AzureDevOpsWorkItemsConnector : ConnectorClientBase
{
    #region Variables

    private AzureDevOpsConfig Config => _appConfig.CurrentValue.Connectors.AzureDevOps;

    private readonly string[] _supportedOperations =
    [
        "list_work_items",
        "create_work_item",
        "update_work_item",
        "create_sprint"
    ];

    [GeneratedRegex(@"^[\w\s\-\.]{1,64}$")]
    private static partial Regex SafeProjectNameRegex();

    #endregion

    #region Constructor

    /// <summary>
    /// Initializes a new instance of <see cref="AzureDevOpsWorkItemsConnector"/>.
    /// </summary>
    public AzureDevOpsWorkItemsConnector(
        ILogger<AzureDevOpsWorkItemsConnector> logger,
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<AppConfig> appConfig)
        : base(logger, httpClientFactory, appConfig)
    {
    }

    #endregion

    #region IConnectorClient Implementation

    /// <inheritdoc/>
    public override string ToolName => "azure_devops_work_items";

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
            case "list_work_items":
                ValidateProject(parameters, errors);
                break;
            case "create_work_item":
                ValidateProject(parameters, errors);
                if (!parameters.ContainsKey("type"))
                    errors.Add("Work item type is required (e.g., 'Bug', 'Task', 'User Story')");
                if (!parameters.ContainsKey("title"))
                    errors.Add("Title is required");
                break;
            case "update_work_item":
                if (!parameters.ContainsKey("id"))
                    errors.Add("Work item ID is required");
                if (!parameters.ContainsKey("updates") && !parameters.ContainsKey("title"))
                    errors.Add("Either 'updates' (JSON Patch) or 'title' is required");
                break;
            case "create_sprint":
                ValidateProject(parameters, errors);
                if (!parameters.ContainsKey("name"))
                    errors.Add("Sprint name is required");
                if (!parameters.ContainsKey("iterationPath"))
                    errors.Add("Iteration path is required (e.g., 'MyProject\\Sprint 1')");
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
            "list_work_items" => await ListWorkItemsAsync(parameters, cancellationToken),
            "create_work_item" => await CreateWorkItemAsync(parameters, cancellationToken),
            "update_work_item" => await UpdateWorkItemAsync(parameters, cancellationToken),
            "create_sprint" => await CreateSprintAsync(parameters, cancellationToken),
            _ => ConnectorOperationResult.Failure($"Operation '{operation}' is not implemented")
        };
    }

    #endregion

    #region Operations

    private async Task<ConnectorOperationResult> ListWorkItemsAsync(
        Dictionary<string, object> parameters,
        CancellationToken cancellationToken)
    {
        var project = GetOptionalParameter<string>(parameters, "project") ?? Config.DefaultProject!;
        var wiql = GetOptionalParameter<string>(parameters, "wiql");
        var top = Math.Clamp(GetOptionalParameter<int>(parameters, "top", 100), 1, 200);
        var skip = Math.Max(GetOptionalParameter<int>(parameters, "skip", 0), 0);

        if (!SafeProjectNameRegex().IsMatch(project))
            return ConnectorOperationResult.Failure("Invalid project name format");

        try
        {
            var httpClient = CreateHttpClient();
            ConfigureHttpClient(httpClient);

            var query = wiql;
            if (string.IsNullOrWhiteSpace(query))
            {
                query = $"SELECT TOP {top} [System.Id], [System.Title], [System.State], " +
                    $"[System.WorkItemType], [System.AssignedTo] FROM WorkItems " +
                    $"WHERE [System.TeamProject] = '{project}' " +
                    $"ORDER BY [System.ChangedDate] DESC SKIP {skip}";
            }

            var queryPayload = new { query };
            var queryContent = CreateJsonContent(queryPayload);

            var config = Config;
            var queryUrl = $"{config.OrganizationUrl}/{Uri.EscapeDataString(project)}/_apis/wit/wiql?api-version={config.ApiVersion}";
            var queryResponse = await httpClient.PostAsync(queryUrl, queryContent, cancellationToken);

            var httpError = await CheckHttpErrorAsync(queryResponse, "Work item query", cancellationToken);
            if (httpError != null) return httpError;

            var queryResult = await queryResponse.Content.ReadAsStringAsync(cancellationToken);
            using var queryDoc = JsonDocument.Parse(queryResult);

            var workItems = new List<WorkItemInfo>();
            if (queryDoc.RootElement.TryGetProperty("workItems", out var wiArray) && wiArray.ValueKind == JsonValueKind.Array)
            {
                var ids = wiArray.EnumerateArray()
                    .Select(x => x.GetProperty("id").GetInt32())
                    .Take(top)
                    .ToList();

                if (ids.Count > 0)
                    workItems = await GetWorkItemsByIdsAsync(httpClient, ids, cancellationToken);
            }

            return ConnectorOperationResult.Success(workItems, BuildWorkItemsMarkdown(workItems));
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error listing work items");
            return ConnectorOperationResult.Failure($"Network error: {ex.Message}");
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "JSON parsing error listing work items");
            return ConnectorOperationResult.Failure($"Response parsing error: {ex.Message}");
        }
    }

    private async Task<ConnectorOperationResult> CreateWorkItemAsync(
        Dictionary<string, object> parameters,
        CancellationToken cancellationToken)
    {
        var project = GetOptionalParameter<string>(parameters, "project") ?? Config.DefaultProject!;
        var type = GetRequiredParameter<string>(parameters, "type");
        var title = GetRequiredParameter<string>(parameters, "title");

        try
        {
            var httpClient = CreateHttpClient();
            ConfigureHttpClient(httpClient);

            var patchOperations = BuildCreatePatchOperations(parameters, title);

            var patchContent = new StringContent(
                JsonSerializer.Serialize(patchOperations, JsonOptions),
                Encoding.UTF8, "application/json-patch+json");

            var config = Config;
            var url = $"{config.OrganizationUrl}/{Uri.EscapeDataString(project)}/_apis/wit/workitems/${Uri.EscapeDataString(type)}?api-version={config.ApiVersion}";
            var response = await httpClient.PostAsync(url, patchContent, cancellationToken);

            var httpError = await CheckHttpErrorAsync(response, "Create work item", cancellationToken);
            if (httpError != null) return httpError;

            var result = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(result);

            var workItemId = doc.RootElement.GetProperty("id").GetInt32();
            var webUrl = doc.RootElement.GetProperty("_links").GetProperty("html").GetProperty("href").GetString();

            var markdown = $"## Work Item Created\n\n" +
                $"**ID:** {workItemId}\n**Type:** {type}\n**Title:** {title}\n" +
                $"**Project:** {project}\n**URL:** {webUrl}\n";

            _logger.LogInformation("Created work item {WorkItemId} in project {Project}", workItemId, project);
            return ConnectorOperationResult.Success(new { id = workItemId, type, title, project, url = webUrl }, markdown);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error creating work item");
            return ConnectorOperationResult.Failure($"Network error: {ex.Message}");
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException)
        {
            _logger.LogError(ex, "Error creating work item");
            return ConnectorOperationResult.Failure($"Error: {ex.Message}");
        }
    }

    private async Task<ConnectorOperationResult> UpdateWorkItemAsync(
        Dictionary<string, object> parameters,
        CancellationToken cancellationToken)
    {
        var id = GetRequiredParameter<int>(parameters, "id");

        try
        {
            var httpClient = CreateHttpClient();
            ConfigureHttpClient(httpClient);

            var patchOperations = BuildUpdatePatchOperations(parameters);

            var patchContent = new StringContent(
                JsonSerializer.Serialize(patchOperations, JsonOptions),
                Encoding.UTF8, "application/json-patch+json");

            var config = Config;
            var url = $"{config.OrganizationUrl}/_apis/wit/workitems/{id}?api-version={config.ApiVersion}";
            var response = await httpClient.PatchAsync(new Uri(url), patchContent, cancellationToken);

            var httpError = await CheckHttpErrorAsync(response, "Update work item", cancellationToken);
            if (httpError != null) return httpError;

            var result = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(result);

            var webUrl = doc.RootElement.GetProperty("_links").GetProperty("html").GetProperty("href").GetString();
            var revision = doc.RootElement.GetProperty("rev").GetInt32();

            var markdown = $"## Work Item Updated\n\n**ID:** {id}\n**Revision:** {revision}\n**URL:** {webUrl}\n";

            _logger.LogInformation("Updated work item {WorkItemId} to revision {Revision}", id, revision);
            return ConnectorOperationResult.Success(new { id, revision, url = webUrl }, markdown);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error updating work item");
            return ConnectorOperationResult.Failure($"Network error: {ex.Message}");
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException)
        {
            _logger.LogError(ex, "Error updating work item");
            return ConnectorOperationResult.Failure($"Error: {ex.Message}");
        }
    }

    private async Task<ConnectorOperationResult> CreateSprintAsync(
        Dictionary<string, object> parameters,
        CancellationToken cancellationToken)
    {
        var project = GetOptionalParameter<string>(parameters, "project") ?? Config.DefaultProject!;
        var name = GetRequiredParameter<string>(parameters, "name");
        var iterationPath = GetRequiredParameter<string>(parameters, "iterationPath");
        var startDate = GetOptionalParameter<DateTime>(parameters, "startDate");
        var finishDate = GetOptionalParameter<DateTime>(parameters, "finishDate");

        try
        {
            var httpClient = CreateHttpClient();
            ConfigureHttpClient(httpClient);

            var iterationPayload = new
            {
                name,
                attributes = new
                {
                    startDate = startDate != default ? startDate.ToString("o") : null,
                    finishDate = finishDate != default ? finishDate.ToString("o") : null
                }
            };

            var config = Config;
            var url = $"{config.OrganizationUrl}/{Uri.EscapeDataString(project)}/_apis/work/teamsettings/iterations?api-version={config.ApiVersion}";
            var response = await httpClient.PostAsync(url, CreateJsonContent(iterationPayload), cancellationToken);

            var httpError = await CheckHttpErrorAsync(response, "Create sprint", cancellationToken);
            if (httpError != null) return httpError;

            var markdown = $"## Sprint Created\n\n**Name:** {name}\n**Iteration Path:** {iterationPath}\n**Project:** {project}\n";
            _logger.LogInformation("Created sprint {SprintName} with path {Path}", name, iterationPath);
            return ConnectorOperationResult.Success(new { name, iterationPath, project }, markdown);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error creating sprint");
            return ConnectorOperationResult.Failure($"Network error: {ex.Message}");
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException)
        {
            _logger.LogError(ex, "Error creating sprint");
            return ConnectorOperationResult.Failure($"Error: {ex.Message}");
        }
    }

    #endregion

    #region Helper Methods

    private void ValidateProject(Dictionary<string, object> parameters, List<string> errors)
    {
        if (!parameters.ContainsKey("project") && string.IsNullOrWhiteSpace(Config.DefaultProject))
            errors.Add("Project is required when DefaultProject is not configured");
    }

    private void ConfigureHttpClient(HttpClient httpClient)
    {
        var config = Config;
        httpClient.Timeout = TimeSpan.FromSeconds(config.TimeoutSeconds);
        httpClient.DefaultRequestHeaders.Clear();
        AddAcceptHeader(httpClient);
        AddBasicAuth(httpClient, string.Empty, config.PersonalAccessToken!);
    }

    private static List<object> BuildCreatePatchOperations(Dictionary<string, object> parameters, string title)
    {
        var ops = new List<object> { new { op = "add", path = "/fields/System.Title", value = title } };

        AddPatchOp(ops, parameters, "description", "/fields/System.Description");
        AddPatchOp(ops, parameters, "assignedTo", "/fields/System.AssignedTo");
        AddPatchOp(ops, parameters, "iterationPath", "/fields/System.IterationPath");
        AddPatchOp(ops, parameters, "areaPath", "/fields/System.AreaPath");
        AddPatchOp(ops, parameters, "tags", "/fields/System.Tags");
        AddPatchOp(ops, parameters, "acceptanceCriteria", "/fields/Microsoft.VSTS.Common.AcceptanceCriteria");

        if (parameters.TryGetValue("priority", out var p) && p is int priority && priority > 0)
            ops.Add(new { op = "add", path = "/fields/Microsoft.VSTS.Common.Priority", value = priority });
        if (parameters.TryGetValue("effort", out var e) && e is int effort && effort > 0)
            ops.Add(new { op = "add", path = "/fields/Microsoft.VSTS.Scheduling.Effort", value = effort });

        return ops;
    }

    private static List<object> BuildUpdatePatchOperations(Dictionary<string, object> parameters)
    {
        if (parameters.TryGetValue("updates", out var updatesObj) && updatesObj is string updatesJson)
        {
            var updates = JsonSerializer.Deserialize<List<object>>(updatesJson);
            return updates ?? [];
        }

        var ops = new List<object>();
        AddReplacePatchOp(ops, parameters, "title", "/fields/System.Title");
        AddReplacePatchOp(ops, parameters, "description", "/fields/System.Description");
        AddReplacePatchOp(ops, parameters, "assignedTo", "/fields/System.AssignedTo");
        AddReplacePatchOp(ops, parameters, "state", "/fields/System.State");
        AddReplacePatchOp(ops, parameters, "iterationPath", "/fields/System.IterationPath");
        AddReplacePatchOp(ops, parameters, "areaPath", "/fields/System.AreaPath");
        AddReplacePatchOp(ops, parameters, "tags", "/fields/System.Tags");
        AddPatchOp(ops, parameters, "comment", "/fields/System.History");

        if (parameters.TryGetValue("priority", out var p) && p is int priority && priority > 0)
            ops.Add(new { op = "replace", path = "/fields/Microsoft.VSTS.Common.Priority", value = priority });

        return ops;
    }

    private static void AddPatchOp(List<object> ops, Dictionary<string, object> parameters, string key, string fieldPath)
    {
        if (parameters.TryGetValue(key, out var val) && val is string s && !string.IsNullOrWhiteSpace(s))
            ops.Add(new { op = "add", path = fieldPath, value = s });
    }

    private static void AddReplacePatchOp(List<object> ops, Dictionary<string, object> parameters, string key, string fieldPath)
    {
        if (parameters.TryGetValue(key, out var val) && val is string s && !string.IsNullOrWhiteSpace(s))
            ops.Add(new { op = "replace", path = fieldPath, value = s });
    }

    private async Task<List<WorkItemInfo>> GetWorkItemsByIdsAsync(
        HttpClient httpClient, List<int> ids, CancellationToken cancellationToken)
    {
        var idsString = string.Join(",", ids);
        var config = Config;
        var getUrl = $"{config.OrganizationUrl}/_apis/wit/workitems?ids={idsString}&api-version={config.ApiVersion}";

        var getResponse = await httpClient.GetAsync(getUrl, cancellationToken);
        if (!getResponse.IsSuccessStatusCode)
        {
            _logger.LogError("Failed to fetch work item details: {StatusCode}", getResponse.StatusCode);
            return ids.Select(id => new WorkItemInfo { Id = id, Title = "[Error loading details]" }).ToList();
        }

        var getResult = await getResponse.Content.ReadAsStringAsync(cancellationToken);
        using var getDoc = JsonDocument.Parse(getResult);

        var workItems = new List<WorkItemInfo>();
        if (getDoc.RootElement.TryGetProperty("value", out var valueArray) && valueArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in valueArray.EnumerateArray())
            {
                var fields = item.GetProperty("fields");
                workItems.Add(new WorkItemInfo
                {
                    Id = item.GetProperty("id").GetInt32(),
                    Title = fields.TryGetProperty("System.Title", out var titleProp)
                        ? titleProp.GetString() ?? "[No Title]" : "[No Title]",
                    Type = fields.TryGetProperty("System.WorkItemType", out var typeProp) ? typeProp.GetString() : null,
                    State = fields.TryGetProperty("System.State", out var stateProp) ? stateProp.GetString() : null,
                    AssignedTo = fields.TryGetProperty("System.AssignedTo", out var assignedProp)
                        && assignedProp.ValueKind == JsonValueKind.Object
                        && assignedProp.TryGetProperty("displayName", out var displayNameProp)
                        ? displayNameProp.GetString() : null,
                    Url = item.GetProperty("_links").GetProperty("html").GetProperty("href").GetString()
                });
            }
        }

        return workItems;
    }

    private static string BuildWorkItemsMarkdown(List<WorkItemInfo> workItems)
    {
        if (workItems.Count == 0)
            return "## Work Items\n\nNo work items found.";

        var sb = new StringBuilder();
        sb.AppendLine($"## Work Items ({workItems.Count})\n");

        foreach (var item in workItems)
        {
            sb.AppendLine($"### [{item.Id}] {item.Title}");
            sb.AppendLine($"- **Type:** {item.Type ?? "N/A"}");
            sb.AppendLine($"- **State:** {item.State ?? "N/A"}");
            if (!string.IsNullOrWhiteSpace(item.AssignedTo))
                sb.AppendLine($"- **Assigned To:** {item.AssignedTo}");
            if (!string.IsNullOrWhiteSpace(item.Url))
                sb.AppendLine($"- **URL:** {item.Url}");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    #endregion

    #region Nested Types

    private sealed class WorkItemInfo
    {
        public int Id { get; init; }
        public required string Title { get; init; }
        public string? Type { get; init; }
        public string? State { get; init; }
        public string? AssignedTo { get; init; }
        public string? Url { get; init; }
    }

    #endregion
}
