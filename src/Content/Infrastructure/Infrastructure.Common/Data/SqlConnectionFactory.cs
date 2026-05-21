using System.Data.Common;
using Application.Common.Interfaces.Data;
using Domain.Common.Config;
using Microsoft.Extensions.Options;

namespace Infrastructure.Common.Data;

/// <summary>
/// Creates database connections using a registered <see cref="DbProviderFactory"/>
/// and connection string from configuration. Uses the provider-agnostic
/// <see cref="DbProviderFactory"/> pattern so consumers are not coupled to a specific
/// database vendor (SQL Server, PostgreSQL, SQLite, etc.).
/// </summary>
/// <remarks>
/// Register via DI by providing the appropriate <see cref="DbProviderFactory"/> for your
/// database vendor. For example, SQL Server uses <c>Microsoft.Data.SqlClient.SqlClientFactory.Instance</c>.
/// The factory itself is registered as a singleton alongside this service.
/// </remarks>
internal sealed class SqlConnectionFactory : ISqlConnectionFactory
{
    private readonly DbProviderFactory _providerFactory;
    private readonly string _connectionString;

    /// <summary>
    /// Initializes a new instance of <see cref="SqlConnectionFactory"/>.
    /// </summary>
    /// <param name="providerFactory">
    /// The provider-specific factory used to create connections.
    /// Must not return <see langword="null"/> from <see cref="DbProviderFactory.CreateConnection"/>.
    /// </param>
    /// <param name="configMonitor">Application configuration monitor.</param>
    public SqlConnectionFactory(DbProviderFactory providerFactory, IOptionsMonitor<AppConfig> configMonitor)
    {
        _providerFactory = providerFactory;
        _connectionString = configMonitor.CurrentValue.AI.Rag.SqlDatabase.ConnectionString;
    }

    /// <inheritdoc/>
    public DbConnection CreateConnection()
    {
        var connection = _providerFactory.CreateConnection()
            ?? throw new InvalidOperationException(
                $"DbProviderFactory '{_providerFactory.GetType().Name}' returned null from CreateConnection().");
        connection.ConnectionString = _connectionString;
        return connection;
    }
}
