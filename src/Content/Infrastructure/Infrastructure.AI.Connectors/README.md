# Infrastructure.AI.Connectors

Agents need to interact with the systems that developers already use — issue trackers, source control, chat platforms. This project provides a unified connector system that wraps third-party APIs (GitHub, Jira, Azure DevOps, Slack) behind a consistent interface, and bridges every connector into the tool pipeline so agents can invoke them like any other tool.

---

## The Connector Pattern

Every connector extends `ConnectorClientBase`, which provides a template method pipeline:

```
ExecuteAsync
  → Check availability (is the service configured?)
    → Validate operation (is this a known operation?)
      → Validate parameters (are required params present?)
        → Dispatch to ExecuteOperationAsync (the actual API call)
          → Handle errors and build result
```

The base class also provides HTTP helpers (auth headers, JSON serialization), parameter extraction utilities (`GetRequiredParameter<T>`, `GetOptionalParameter<T>`), and markdown summary generation so agents get human-readable results, not raw JSON.

All operations return `ConnectorOperationResult` — a Result-like type with success/failure, data payload, and formatted markdown. No exceptions for expected failures.

## The Five Connectors

### Azure DevOps Work Items
4 operations: `list_work_items` (WIQL queries), `create_work_item`, `update_work_item` (JSON Patch), `create_sprint`. Uses PAT-based Basic auth. Validates project names with regex to prevent injection.

### GitHub Issues
4 operations: `list_issues`, `create_issue`, `update_issue`, `close_issue` (with optional closing comment). Bearer token auth. Handles labels, pagination, and generates markdown reports.

### GitHub Repos
4 operations: `create_repository`, `configure_settings`, `add_collaborator`, `list_repositories`. Supports both user and organization endpoints. Validates permission levels.

### Jira Issues
4 operations: `list_issues` (JQL), `create_issue`, `update_issue`, `transition_issue`. Email + API token Basic auth. Builds Atlassian Document Format (ADF) payloads for descriptions. Supports comments on transitions.

### Slack Notifications
2 operations: `send_message` (via Bot Token or Webhook, with block support and threading), `upload_file` (Bot Token only, Base64 content). Gracefully handles both auth modes.

## Tool Pipeline Integration

The key architectural decision: connectors are registered both as `IConnectorClient` (for direct use) and as `ITool` via keyed DI. The `DependencyInjection.cs` class bridges each connector into the tool pipeline using `ConnectorToolAdapter`, which wraps the connector's operation dispatch behind the standard `ITool` interface.

This means an agent doesn't need special code to use GitHub or Jira — they appear as tools alongside file system operations and MCP tools. The agent calls them the same way it calls anything else.

The `ConnectorClientFactory` provides lookup and discovery: get a connector by name, list all available connectors, or filter for only those with valid configuration.

---

## Project Structure

```
Infrastructure.AI.Connectors/
├── Core/
│   ├── ConnectorClientBase.cs        Template method pipeline + HTTP helpers
│   └── ConnectorClientFactory.cs     Connector discovery and lookup
├── AzureDevOps/
│   └── AzureDevOpsWorkItemsConnector.cs  Work items (WIQL, JSON Patch)
├── GitHub/
│   ├── GitHubIssuesConnector.cs      Issues (CRUD, labels, pagination)
│   └── GitHubReposConnector.cs       Repositories (create, configure, collaborate)
├── Jira/
│   └── JiraIssuesConnector.cs        Issues (JQL, ADF payloads, transitions)
├── Slack/
│   └── SlackNotificationsConnector.cs  Messages + file upload (Bot/Webhook)
└── DependencyInjection.cs            Registers connectors + ITool bridge
```

## Dependencies

- **Application.AI.Common** — `IConnectorClient`, `IConnectorClientFactory`, `ConnectorToolAdapter`, `ITool`
- **Domain.Common** — `AppConfig.Connectors` configuration
- **Ardalis.GuardClauses** — Parameter validation
- **Microsoft.Extensions.Http** — `IHttpClientFactory` for managed HTTP lifecycle
