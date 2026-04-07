namespace Domain.Common.Config.Infrastructure.ContentProvider;

/// <summary>
/// Specifies the available content provider types.
/// </summary>
/// <remarks>
/// <para><strong>Adding New Providers:</strong></para>
/// <list type="number">
///   <item><description>Add the enum value here</description></item>
///   <item><description>Create provider config class (e.g., <c>HttpContentProviderConfig</c>)</description></item>
///   <item><description>Create implementation of <c>IContentProvider</c></description></item>
///   <item><description>Update DI registration to handle the new type</description></item>
/// </list>
/// </remarks>
public enum ContentProviderType
{
    /// <summary>
    /// Reads content from the local file system.
    /// </summary>
    /// <remarks>
    /// Default provider. Configuration via <see cref="FileSystemContentProviderConfig"/>.
    /// </remarks>
    FileSystem,

    /// <summary>
    /// Reads content from a database. Reserved for future implementation.
    /// </summary>
    Database,

    /// <summary>
    /// Reads content from HTTP/HTTPS endpoints. Reserved for future implementation.
    /// </summary>
    Http,

    /// <summary>
    /// In-memory content storage for testing purposes. Reserved for future implementation.
    /// </summary>
    InMemory,
}

/// <summary>
/// Configuration specific to the file system content provider.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Configuration Example:</strong>
/// <code>
/// {
///   "Infrastructure": {
///     "ContentProvider": {
///       "ProviderType": "FileSystem",
///       "FileSystem": {
///         "BaseDirectory": "C:/data/content",
///         "WatchForChanges": true,
///         "FilePatterns": [ "*.md", "*.json" ]
///       }
///     }
///   }
/// }
/// </code>
/// </para>
/// </remarks>
public class FileSystemContentProviderConfig
{
    /// <summary>
    /// Gets or sets the base directory for content files.
    /// </summary>
    public string BaseDirectory { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets whether to watch for file changes and reload content automatically.
    /// </summary>
    /// <value>Default: <c>false</c>.</value>
    public bool WatchForChanges { get; set; }

    /// <summary>
    /// Gets or sets the file patterns to include when scanning for content.
    /// </summary>
    /// <value>Default: <c>["*.md", "*.json"]</c>.</value>
    public List<string> FilePatterns { get; set; } = ["*.md", "*.json"];
}
