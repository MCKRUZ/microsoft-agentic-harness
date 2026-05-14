# Infrastructure.AI.Connectors

## What This Project Is

Infrastructure.AI.Connectors provides pre-built integrations with external services (GitHub, Jira, Azure DevOps, Slack) that AI agents can invoke as tools during conversations. When an agent needs to create a GitHub issue, list Jira tickets, or send a Slack notification, it calls a connector -- and this project handles authentication, HTTP communication, parameter validation, error handling, and response formatting for each service.

The problem it solves: without connectors, every external service integration would require custom tool code, duplicating HTTP client management, auth patterns, and error handling. Connectors provide a standardized abstraction so adding a new external service is a matter of implementing one class with a known shape.

This project depends on Application.AI.Common (for `IConnectorClient` and `ITool` interfaces) and Domain.Common (for configuration). It is referenced by Presentation hosts that register the DI extensions.

**Analogy:** If the agent harness is a software engineer, connectors are its IDE plugins -- each one gives it access to a new external system through a consistent interface.

## Architecture Context

```
Application.AI.Common
  IConnectorClient            Domain.Common
  IConnectorClientFactory       ConnectorsConfig
  ITool                              |
       |                             |
       v                             v
+-----------------------------------------------+
|        Infrastructure.AI.Connectors            |
|                                                |
|  ConnectorClientBase (abstract)                |
|    |-- GitHubReposConnector                    |
|    |-- GitHubIssuesConnector                   |
|    |-- JiraIssuesConnector                     |
|    |-- AzureDevOpsWorkItemsConnector           |
|    |-- SlackNotificationsConnector             |
|                                                |
|  ConnectorClientFactory (lookup by name)       |
|  ConnectorToolAdapter (bridges to ITool)       |
+-----------------------------------------------+
         ^
         |
  Presentation hosts call:
  services.AddAIConnectors();
```

## Key Concepts

### ConnectorClientBase (The Template Method Pattern)

**What it is:** An abstract base class that handles the common execution pipeline for all connectors.

**Why it exists:** Every connector needs the same workflow: check if configured, validate the operation name, validate parameters, execute, handle errors. Without a base class, each connector would duplicate 100+ lines of boilerplate.

**How it works:**

```
ExecuteAsync(operation, parameters)
    |
    v
[Is connector available?] --no--> Return "not configured" failure
    |yes
    v
[Is operation supported?] --no--> Return "unsupported operation" failure
    |yes
    v
[Validate parameters]     --errors--> Return validation failure
    |valid
    v
[ExecuteOperationAsync]   --exception--> Return failure with message
    |success
    v
Return ConnectorOperationResult.Success(data)
```

The base class provides:
- `IHttpClientFactory` for named HTTP clients per connector
- Helper methods: `GetRequiredParameter<T>()`, `GetOptionalParameter<T>()`, `AddBearerAuth()`, `AddBasicAuth()`, `CreateJsonContent<T>()`, `CheckHttpErrorAsync()`
- Standard JSON serializer options (case-insensitive, indented)
- `ExecuteWithErrorHandlingAsync()` wrapping HTTP and JSON exceptions

Derived classes implement four things:
1. `ToolName` -- unique identifier (e.g., `"github_repos"`)
2. `IsAvailable` -- checks if the connector's config section has credentials
3. `SupportedOperations` -- whitelist of allowed operations (e.g., `["list", "get", "create"]`)
4. `ExecuteOperationAsync()` -- the actual HTTP call logic

### ConnectorClientFactory

**What it is:** A runtime lookup service that resolves connectors by tool name.

**Why it exists:** The agent harness discovers tools by name at runtime. When the LLM requests `github_issues.create`, the harness needs to find the right connector without compile-time coupling.

**How it works:** At construction, it builds a dictionary from all `IConnectorClient` instances registered in DI, keyed by `ToolName`. Three methods:
- `GetClient(toolName)` -- single lookup, returns null if not found
- `GetAllClients()` -- all registered connectors (configured or not)
- `GetAvailableClients()` -- only connectors where `IsAvailable` is true (re-evaluated at call time since config can change via `IOptionsMonitor`)

### Tool Bridge via Keyed DI

The DI registration bridges each connector into the `ITool` interface using keyed singletons:

```csharp
RegisterConnectorAsTool(services, "github_issues");
// Internally creates a ConnectorToolAdapter wrapping the resolved connector
```

This means the agent sees `github_repos`, `jira_issues`, etc. in its tool list alongside built-in tools like `file_system` and `document_search`.

### Connector Implementations

| Connector | Tool Name | Operations | External Service |
|-----------|-----------|-----------|-----------------|
| `GitHubReposConnector` | `github_repos` | list, get, search | GitHub REST API |
| `GitHubIssuesConnector` | `github_issues` | list, get, create, update | GitHub REST API |
| `JiraIssuesConnector` | `jira_issues` | list, get, create, transition | Jira Cloud REST API |
| `AzureDevOpsWorkItemsConnector` | `azure_devops_work_items` | list, get, create, update | Azure DevOps REST API |
| `SlackNotificationsConnector` | `slack_notifications` | send, send_blocks | Slack Web API |

## Data Flow

```
Agent requests tool: "github_issues"
       |
       v
[Keyed DI resolves ITool("github_issues")]
       |
       v
[ConnectorToolAdapter.ExecuteAsync()]
       |
       v
[ConnectorClientFactory.GetClient("github_issues")]
       |
       v
[GitHubIssuesConnector.ExecuteAsync("create", {...})]
       |
       v
[ConnectorClientBase pipeline:]
  1. IsAvailable? (checks config for PAT/token)
  2. Operation in SupportedOperations?
  3. ValidateParametersAsync() (required fields present?)
  4. ExecuteOperationAsync() (actual HTTP call to GitHub)
       |
       v
[ConnectorOperationResult] --> returned to agent as tool output
```

## Project Structure

```
Infrastructure.AI.Connectors/
├── Core/
│   ├── ConnectorClientBase.cs       Abstract base with execution pipeline + HTTP helpers
│   └── ConnectorClientFactory.cs    Runtime connector lookup by tool name
├── AzureDevOps/
│   └── AzureDevOpsWorkItemsConnector.cs   Azure DevOps work item operations
├── GitHub/
│   ├── GitHubReposConnector.cs      Repository listing, search, metadata
│   └── GitHubIssuesConnector.cs     Issue CRUD operations
├── Jira/
│   └── JiraIssuesConnector.cs       Jira issue operations with transitions
├── Slack/
│   └── SlackNotificationsConnector.cs  Message sending (text + Block Kit)
├── DependencyInjection.cs           Registers all connectors + factory + tool bridges
└── Infrastructure.AI.Connectors.csproj
```

## Key Types Reference

| Type | Purpose | Implements | Lifetime |
|------|---------|-----------|----------|
| `ConnectorClientBase` | Abstract execution pipeline | `IConnectorClient` | -- |
| `ConnectorClientFactory` | Runtime connector lookup | `IConnectorClientFactory` | Singleton |
| `GitHubReposConnector` | GitHub repository operations | `IConnectorClient` | Singleton |
| `GitHubIssuesConnector` | GitHub issue CRUD | `IConnectorClient` | Singleton |
| `JiraIssuesConnector` | Jira issue operations | `IConnectorClient` | Singleton |
| `AzureDevOpsWorkItemsConnector` | ADO work item ops | `IConnectorClient` | Singleton |
| `SlackNotificationsConnector` | Slack messaging | `IConnectorClient` | Singleton |

## Configuration

Each connector reads credentials from `AppConfig.Connectors`:

```jsonc
{
  "AppConfig": {
    "Connectors": {
      "GitHub": {
        "PersonalAccessToken": "ghp_...",    // Required for GitHub connectors
        "BaseUrl": "https://api.github.com", // Optional override for GitHub Enterprise
        "DefaultOrg": "my-org"               // Optional default organization
      },
      "Jira": {
        "BaseUrl": "https://mycompany.atlassian.net",
        "Email": "bot@company.com",          // Jira Cloud basic auth
        "ApiToken": "ATATT3..."              // Jira API token
      },
      "AzureDevOps": {
        "OrganizationUrl": "https://dev.azure.com/myorg",
        "PersonalAccessToken": "...",
        "DefaultProject": "MyProject"
      },
      "Slack": {
        "BotToken": "xoxb-...",              // Slack Bot User OAuth Token
        "DefaultChannel": "#agent-notifications"
      }
    }
  }
}
```

When credentials are missing, `IsAvailable` returns false and the connector gracefully reports "not configured" instead of throwing.

## Common Tasks

### How to Add a New Connector

1. **Create a config class** in `Domain.Common/Config/Connectors/` with the service's credentials:
```csharp
public sealed class MyServiceConfig
{
    public string? ApiKey { get; init; }
    public string? BaseUrl { get; init; }
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);
}
```

2. **Add the property** to `ConnectorsConfig` in Domain.Common.

3. **Create the connector class** extending `ConnectorClientBase`:
```csharp
public sealed class MyServiceConnector : ConnectorClientBase
{
    public override string ToolName => "my_service";
    public override bool IsAvailable =>
        _appConfig.CurrentValue.Connectors.MyService.IsConfigured;
    public override IReadOnlyList<string> SupportedOperations =>
        ["list", "get", "create"];

    protected override async Task<ConnectorOperationResult> ExecuteOperationAsync(
        string operation, Dictionary<string, object> parameters, CancellationToken ct)
    {
        return operation switch
        {
            "list" => await ListItemsAsync(parameters, ct),
            "get" => await GetItemAsync(parameters, ct),
            "create" => await CreateItemAsync(parameters, ct),
            _ => ConnectorOperationResult.Failure($"Unknown operation: {operation}")
        };
    }
}
```

4. **Register in DependencyInjection.cs:**
```csharp
services.AddSingleton<IConnectorClient, MyServiceConnector>();
RegisterConnectorAsTool(services, "my_service");
```

### How to Debug a Connector Failure

1. Check structured logs for `"Connector '{ToolName}' is not available"` -- indicates missing config.
2. Check for `"Parameter validation failed"` -- indicates the agent sent wrong parameters.
3. Check for `"Connector operation failed"` -- indicates an HTTP error from the external service.
4. The `ConnectorOperationResult` includes HTTP status codes for error responses, making it easy to distinguish auth failures (401/403) from not-found (404) from server errors (500+).

## Dependencies

**Project References:**
- `Application.AI.Common` -- `IConnectorClient`, `IConnectorClientFactory`, `ITool` interfaces
- `Domain.Common` -- `AppConfig`, `ConnectorsConfig` for credential access

**NuGet Packages:**
- `Ardalis.GuardClauses` -- Parameter null-checking in constructors
- `Microsoft.Extensions.Http` -- `IHttpClientFactory` for managed HTTP clients
- `Microsoft.Extensions.Logging.Abstractions` -- Structured logging
- `Microsoft.Extensions.Options` -- `IOptionsMonitor<AppConfig>` for runtime config

## Testing

- **Test project:** `Infrastructure.AI.Connectors.Tests`
- **Run:** `dotnet test --filter "FullyQualifiedName~Infrastructure.AI.Connectors.Tests"`
- **Mock guidance:** Mock `IHttpClientFactory` to return `HttpClient` with a custom `HttpMessageHandler` that returns canned JSON responses. Test the full `ConnectorClientBase` pipeline (validation, dispatch, error handling) with real connector instances and mocked HTTP. Never call real external APIs in unit tests.
