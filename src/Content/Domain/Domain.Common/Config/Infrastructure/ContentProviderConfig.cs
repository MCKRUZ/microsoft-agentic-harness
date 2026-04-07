using Domain.Common.Config.Infrastructure.ContentProvider;

namespace Domain.Common.Config.Infrastructure;

/// <summary>
/// Configuration for content providers that abstract content access from various sources.
/// </summary>
/// <remarks>
/// <para><strong>Provider Types:</strong></para>
/// <list type="bullet">
///   <item><description><strong>FileSystem</strong> — Reads from local file system</description></item>
///   <item><description><strong>Database</strong> — Reads from database (future)</description></item>
///   <item><description><strong>Http</strong> — Reads from HTTP endpoints (future)</description></item>
///   <item><description><strong>InMemory</strong> — In-memory content for testing (future)</description></item>
/// </list>
/// <para>
/// <strong>Configuration Example:</strong>
/// <code>
/// {
///   "Infrastructure": {
///     "ContentProvider": {
///       "ProviderType": "FileSystem",
///       "FileSystem": {
///         "BaseDirectory": "C:/data/content"
///       }
///     }
///   }
/// }
/// </code>
/// </para>
/// </remarks>
public class ContentProviderConfig
{
    /// <summary>
    /// Gets or sets the type of content provider to use.
    /// </summary>
    /// <value>Default: <see cref="ContentProviderType.FileSystem"/>.</value>
    public ContentProviderType ProviderType { get; set; } = ContentProviderType.FileSystem;

    /// <summary>
    /// Gets or sets the file system-specific configuration.
    /// </summary>
    /// <remarks>
    /// Only used when <see cref="ProviderType"/> is <see cref="ContentProviderType.FileSystem"/>.
    /// </remarks>
    public FileSystemContentProviderConfig FileSystem { get; set; } = new();
}
