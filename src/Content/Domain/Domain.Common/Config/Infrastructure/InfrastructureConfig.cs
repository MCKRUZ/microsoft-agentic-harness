using Domain.Common.Config.Infrastructure.ContentProvider;

namespace Domain.Common.Config.Infrastructure;

/// <summary>
/// Configuration for Infrastructure services including state management and content providers.
/// </summary>
/// <remarks>
/// <para>
/// Configuration hierarchy:
/// <code>
/// AppConfig.Infrastructure
/// ├── StateManagement  — Workflow state persistence (JSON checkpoints, Markdown)
/// ├── ContentProvider  — Content source abstraction (FileSystem, Database, HTTP)
/// └── FileSystem       — Sandboxed file system access for agent tools
/// </code>
/// </para>
/// </remarks>
public class InfrastructureConfig
{
    /// <summary>
    /// Gets or sets the state management configuration.
    /// </summary>
    public StateManagementConfig StateManagement { get; set; } = new();

    /// <summary>
    /// Gets or sets the content provider configuration.
    /// </summary>
    /// <remarks>
    /// Content providers abstract the source of content (files, database, HTTP, etc.)
    /// behind a unified <c>IContentProvider</c> interface.
    /// </remarks>
    public ContentProviderConfig ContentProvider { get; set; } = new();

    /// <summary>
    /// Gets or sets the file system tool configuration.
    /// </summary>
    public FileSystemConfig FileSystem { get; set; } = new();
}

/// <summary>
/// Configuration for the sandboxed file system tool exposed to agents.
/// </summary>
public class FileSystemConfig
{
    /// <summary>
    /// Gets or sets the list of base paths the agent file system tool is allowed to access.
    /// All file operations are restricted to paths beneath these directories.
    /// </summary>
    /// <value>
    /// Default: empty — inherits <c>Logging.LogsBasePath</c> only.
    /// Add project source paths to allow agents to search and read source files.
    /// </value>
    public IReadOnlyList<string> AllowedBasePaths { get; set; } = [];
}
