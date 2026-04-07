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
/// └── ContentProvider  — Content source abstraction (FileSystem, Database, HTTP)
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
}
