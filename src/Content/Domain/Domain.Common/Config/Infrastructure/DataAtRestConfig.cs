namespace Domain.Common.Config.Infrastructure;

/// <summary>
/// Configuration for the deployer's protection of data at rest (#904). Bound to <c>AppConfig:Infrastructure:DataAtRest</c>.
/// </summary>
/// <remarks>
/// The harness keeps personal data in local files it does not encrypt: SQLite databases, JSONL audit trails, log files
/// and spilled tool results, among others. Owner-only file permissions apply on Linux; on Windows files inherit the
/// folder's access control. Encrypting the volume they sit on is the deployer's job, and the options for each store
/// are in the security guide's data-protection chapter.
/// </remarks>
public sealed class DataAtRestConfig
{
    /// <summary>
    /// How the local data files are protected. <see cref="DataAtRestEncryption.Unspecified"/> (the default) warns at
    /// startup outside Development; <see cref="DataAtRestEncryption.Volume"/> records that the volume is encrypted.
    /// </summary>
    public DataAtRestEncryption Encryption { get; set; } = DataAtRestEncryption.Unspecified;
}
