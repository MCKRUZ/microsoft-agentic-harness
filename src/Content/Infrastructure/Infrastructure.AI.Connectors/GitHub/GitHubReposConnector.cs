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
/// Connector client for GitHub repository operations.
/// Supports creating repositories, configuring settings,
/// managing collaborators, and listing repositories via the GitHub REST API v3.
/// </summary>
public sealed class GitHubReposConnector : ConnectorClientBase
{
    #region Variables

    private GitHubConfig Config => _appConfig.CurrentValue.Connectors.GitHub;

    private static readonly string[] ValidPermissions = ["pull", "push", "admin", "maintain", "triage"];

    private readonly string[] _supportedOperations =
    [
        "create_repository",
        "configure_settings",
        "add_collaborator",
        "list_repositories"
    ];

    #endregion

    #region Constructor

    /// <summary>
    /// Initializes a new instance of <see cref="GitHubReposConnector"/>.
    /// </summary>
    public GitHubReposConnector(
        ILogger<GitHubReposConnector> logger,
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<AppConfig> appConfig)
        : base(logger, httpClientFactory, appConfig)
    {
    }

    #endregion

    #region IConnectorClient Implementation

    /// <inheritdoc/>
    public override string ToolName => "github_repos";

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
            case "create_repository":
                if (!parameters.ContainsKey("name"))
                    errors.Add("Repository name is required");
                break;
            case "configure_settings":
                if (!parameters.ContainsKey("owner") && string.IsNullOrWhiteSpace(Config.DefaultOwner))
                    errors.Add("Owner is required when DefaultOwner is not configured");
                if (!parameters.ContainsKey("repo"))
                    errors.Add("Repository name is required");
                break;
            case "add_collaborator":
                if (!parameters.ContainsKey("owner") && string.IsNullOrWhiteSpace(Config.DefaultOwner))
                    errors.Add("Owner is required when DefaultOwner is not configured");
                if (!parameters.ContainsKey("repo"))
                    errors.Add("Repository name is required");
                if (!parameters.ContainsKey("username"))
                    errors.Add("Collaborator username is required");
                if (parameters.TryGetValue("permission", out var perm) && perm is string permStr
                    && !ValidPermissions.Contains(permStr.ToLower()))
                    errors.Add($"Invalid permission. Valid values: {string.Join(", ", ValidPermissions)}");
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
            "create_repository" => await CreateRepositoryAsync(parameters, cancellationToken),
            "configure_settings" => await ConfigureSettingsAsync(parameters, cancellationToken),
            "add_collaborator" => await AddCollaboratorAsync(parameters, cancellationToken),
            "list_repositories" => await ListRepositoriesAsync(parameters, cancellationToken),
            _ => ConnectorOperationResult.Failure($"Operation '{operation}' is not implemented")
        };
    }

    #endregion

    #region Operations

    private async Task<ConnectorOperationResult> CreateRepositoryAsync(
        Dictionary<string, object> parameters,
        CancellationToken cancellationToken)
    {
        var name = GetRequiredParameter<string>(parameters, "name");
        var description = GetOptionalParameter<string>(parameters, "description");
        var @private = GetOptionalParameter<bool>(parameters, "private", false);
        var autoInit = GetOptionalParameter<bool>(parameters, "autoInit", false);

        try
        {
            var httpClient = CreateHttpClient();
            ConfigureHttpClient(httpClient);

            var payload = new { name, description, @private, auto_init = autoInit, has_issues = true };
            var url = $"{Config.BaseUrl}/user/repos";
            var response = await httpClient.PostAsync(url, CreateJsonContent(payload), cancellationToken);

            var httpError = await CheckHttpErrorAsync(response, "Create repository", cancellationToken);
            if (httpError != null) return httpError;

            var result = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(result);

            var repoName = doc.RootElement.GetProperty("name").GetString();
            var fullName = doc.RootElement.GetProperty("full_name").GetString();
            var htmlUrl = doc.RootElement.GetProperty("html_url").GetString();
            var cloneUrl = doc.RootElement.GetProperty("clone_url").GetString();
            var isPrivate = doc.RootElement.GetProperty("private").GetBoolean();

            var markdown = $"## Repository Created\n\n**Name:** {repoName}\n**Full Name:** {fullName}\n" +
                $"**Private:** {isPrivate}\n**URL:** {htmlUrl}\n**Clone URL:** `{cloneUrl}`\n";

            _logger.LogInformation("Created GitHub repository {FullName}", fullName);
            return ConnectorOperationResult.Success(
                new { name = repoName, full_name = fullName, url = htmlUrl, clone_url = cloneUrl, @private = isPrivate }, markdown);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error creating repository");
            return ConnectorOperationResult.Failure($"Network error: {ex.Message}");
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException)
        {
            _logger.LogError(ex, "Error creating repository");
            return ConnectorOperationResult.Failure($"Error: {ex.Message}");
        }
    }

    private async Task<ConnectorOperationResult> ConfigureSettingsAsync(
        Dictionary<string, object> parameters,
        CancellationToken cancellationToken)
    {
        var config = Config;
        var owner = Uri.EscapeDataString(GetOptionalParameter<string>(parameters, "owner") ?? config.DefaultOwner!);
        var repo = Uri.EscapeDataString(GetRequiredParameter<string>(parameters, "repo"));

        try
        {
            var httpClient = CreateHttpClient();
            ConfigureHttpClient(httpClient);

            var payload = new Dictionary<string, object?>();
            var description = GetOptionalParameter<string>(parameters, "description");
            var defaultBranch = GetOptionalParameter<string>(parameters, "defaultBranch");
            if (description != null) payload["description"] = description;
            if (defaultBranch != null) payload["default_branch"] = defaultBranch;

            if (payload.Count == 0)
                return ConnectorOperationResult.Failure("At least one setting must be provided to update");

            var url = $"{config.BaseUrl}/repos/{owner}/{repo}";
            var response = await httpClient.PatchAsync(new Uri(url), CreateJsonContent(payload), cancellationToken);

            var httpError = await CheckHttpErrorAsync(response, "Configure settings", cancellationToken);
            if (httpError != null) return httpError;

            var result = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(result);

            var fullName = doc.RootElement.GetProperty("full_name").GetString();
            var htmlUrl = doc.RootElement.GetProperty("html_url").GetString();

            _logger.LogInformation("Updated settings for GitHub repository {FullName}", fullName);
            return ConnectorOperationResult.Success(
                new { full_name = fullName, url = htmlUrl },
                $"## Repository Settings Updated\n\n**Repository:** {fullName}\n**URL:** {htmlUrl}\n");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error configuring repository settings");
            return ConnectorOperationResult.Failure($"Network error: {ex.Message}");
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException)
        {
            _logger.LogError(ex, "Error configuring repository settings");
            return ConnectorOperationResult.Failure($"Error: {ex.Message}");
        }
    }

    private async Task<ConnectorOperationResult> AddCollaboratorAsync(
        Dictionary<string, object> parameters,
        CancellationToken cancellationToken)
    {
        var config = Config;
        var owner = Uri.EscapeDataString(GetOptionalParameter<string>(parameters, "owner") ?? config.DefaultOwner!);
        var repo = Uri.EscapeDataString(GetRequiredParameter<string>(parameters, "repo"));
        var username = Uri.EscapeDataString(GetRequiredParameter<string>(parameters, "username"));
        var permission = GetOptionalParameter<string>(parameters, "permission", "pull")!.ToLower();

        try
        {
            var httpClient = CreateHttpClient();
            ConfigureHttpClient(httpClient);

            var url = $"{config.BaseUrl}/repos/{owner}/{repo}/collaborators/{username}";
            var response = await httpClient.PutAsync(url, CreateJsonContent(new { permission }), cancellationToken);

            if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.NoContent)
            {
                var error = await response.Content.ReadAsStringAsync(cancellationToken);
                return ConnectorOperationResult.Failure(
                    $"Add collaborator failed: {response.StatusCode} - {error}", (int)response.StatusCode);
            }

            var markdown = $"## Collaborator Added\n\n**Repository:** {owner}/{repo}\n" +
                $"**User:** {username}\n**Permission:** {permission}\n";

            _logger.LogInformation("Added collaborator {Username} to {Owner}/{Repo}", username, owner, repo);
            return ConnectorOperationResult.Success(new { repository = $"{owner}/{repo}", username, permission }, markdown);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error adding collaborator");
            return ConnectorOperationResult.Failure($"Network error: {ex.Message}");
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException)
        {
            _logger.LogError(ex, "Error adding collaborator");
            return ConnectorOperationResult.Failure($"Error: {ex.Message}");
        }
    }

    private async Task<ConnectorOperationResult> ListRepositoriesAsync(
        Dictionary<string, object> parameters,
        CancellationToken cancellationToken)
    {
        var config = Config;
        var owner = GetOptionalParameter<string>(parameters, "owner");
        var sort = GetOptionalParameter<string>(parameters, "sort", "updated");
        var perPage = GetOptionalParameter<int>(parameters, "perPage", 30);

        try
        {
            var httpClient = CreateHttpClient();
            ConfigureHttpClient(httpClient);

            var url = !string.IsNullOrWhiteSpace(owner)
                ? $"{config.BaseUrl}/orgs/{Uri.EscapeDataString(owner)}/repos?sort={sort}&per_page={perPage}"
                : $"{config.BaseUrl}/user/repos?sort={sort}&per_page={perPage}";

            var response = await httpClient.GetAsync(url, cancellationToken);

            var httpError = await CheckHttpErrorAsync(response, "List repositories", cancellationToken);
            if (httpError != null) return httpError;

            var result = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(result);

            var repos = ParseReposFromJson(doc);
            _logger.LogInformation("Listed {Count} repositories", repos.Count);
            return ConnectorOperationResult.Success(repos, BuildRepositoriesMarkdown(repos, owner));
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error listing repositories");
            return ConnectorOperationResult.Failure($"Network error: {ex.Message}");
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "JSON parsing error listing repositories");
            return ConnectorOperationResult.Failure($"Response parsing error: {ex.Message}");
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

    private static List<RepositoryInfo> ParseReposFromJson(JsonDocument doc)
    {
        var repos = new List<RepositoryInfo>();
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return repos;

        foreach (var item in doc.RootElement.EnumerateArray())
        {
            repos.Add(new RepositoryInfo
            {
                Name = item.GetProperty("name").GetString() ?? string.Empty,
                FullName = item.GetProperty("full_name").GetString() ?? string.Empty,
                Private = item.TryGetProperty("private", out var privateProp) && privateProp.GetBoolean(),
                Description = item.TryGetProperty("description", out var descProp) ? descProp.GetString() : null,
                Language = item.TryGetProperty("language", out var langProp) ? langProp.GetString() : null,
                Stars = item.TryGetProperty("stargazers_count", out var starsProp) ? starsProp.GetInt32() : 0,
                Url = item.TryGetProperty("html_url", out var urlProp) ? urlProp.GetString() : null
            });
        }
        return repos;
    }

    private static string BuildRepositoriesMarkdown(List<RepositoryInfo> repos, string? owner)
    {
        if (repos.Count == 0)
            return $"## Repositories\n\nNo repositories found" +
                (!string.IsNullOrWhiteSpace(owner) ? $" for {owner}" : " for authenticated user") + ".";

        var sb = new StringBuilder();
        var title = !string.IsNullOrWhiteSpace(owner) ? $"Repositories for {owner}" : "Your Repositories";
        sb.AppendLine($"## {title} ({repos.Count})\n");

        foreach (var repo in repos)
        {
            sb.AppendLine($"### {repo.FullName}");
            if (!string.IsNullOrWhiteSpace(repo.Description))
                sb.AppendLine(repo.Description);
            sb.AppendLine($"- **Language:** {repo.Language ?? "N/A"} | **Stars:** {repo.Stars}");
            if (!string.IsNullOrWhiteSpace(repo.Url))
                sb.AppendLine($"- **URL:** {repo.Url}");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    #endregion

    #region Nested Types

    private sealed class RepositoryInfo
    {
        public required string Name { get; init; }
        public required string FullName { get; init; }
        public bool Private { get; init; }
        public string? Description { get; init; }
        public string? Language { get; init; }
        public int Stars { get; init; }
        public string? Url { get; init; }
    }

    #endregion
}
