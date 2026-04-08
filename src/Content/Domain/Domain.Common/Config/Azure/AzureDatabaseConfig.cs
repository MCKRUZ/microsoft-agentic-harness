namespace Domain.Common.Config.Azure;

/// <summary>
/// Azure database services configuration.
/// </summary>
public class AzureDatabaseConfig
{
    /// <summary>
    /// Gets or sets the Azure SQL Server client configuration.
    /// </summary>
    public AzureSQLClientConfig AzureSQLClient { get; set; } = new();

    /// <summary>
    /// Gets or sets the Azure Blob Storage configuration.
    /// </summary>
    public AzureBlobStorageConfig AzureBlobStorage { get; set; } = new();
}

/// <summary>
/// Azure SQL Server connection configuration.
/// </summary>
public class AzureSQLClientConfig
{
    /// <summary>
    /// Gets or sets the SQL Server connection string.
    /// When null, SQL health checks are disabled.
    /// </summary>
    public string? ConnectionString { get; set; }
}

/// <summary>
/// Azure Blob Storage connection configuration.
/// </summary>
public class AzureBlobStorageConfig
{
    /// <summary>
    /// Gets or sets the storage account name.
    /// </summary>
    public string? Account { get; set; }

    /// <summary>
    /// Gets or sets the Blob Storage connection string.
    /// When null, Blob health checks are disabled.
    /// </summary>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// Gets or sets the default container name.
    /// </summary>
    public string? Container { get; set; }

    /// <summary>
    /// Gets or sets the endpoint suffix for the storage account.
    /// </summary>
    public string EndpointSuffix { get; set; } = "core.windows.net";
}
