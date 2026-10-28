namespace Domain.Common.Config.Infrastructure;

/// <summary>
/// How the deployer has protected the local files this host writes personal data to (#904). The harness does not
/// encrypt them itself, so this records the deployer's answer rather than anything the harness can detect.
/// </summary>
public enum DataAtRestEncryption
{
    /// <summary>Not stated. Outside Development the host warns at startup.</summary>
    Unspecified = 0,

    /// <summary>
    /// The volume or disk the host's data directory sits on is encrypted (for example BitLocker, LUKS, or a cloud
    /// provider's disk encryption). Covers every local store at once: databases, audit trails, logs and spilled files.
    /// </summary>
    Volume = 1,
}
