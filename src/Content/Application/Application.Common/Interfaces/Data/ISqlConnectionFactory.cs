using System.Data.Common;

namespace Application.Common.Interfaces.Data;

/// <summary>
/// Creates database connections for SQL operations.
/// Implementations handle connection string resolution and provider selection.
/// Each call returns a new, unopened connection — callers own the lifetime.
/// </summary>
public interface ISqlConnectionFactory
{
    /// <summary>
    /// Creates a new database connection using the configured provider and connection string.
    /// The returned connection is NOT opened — the caller must call <see cref="DbConnection.OpenAsync"/>.
    /// </summary>
    DbConnection CreateConnection();
}
