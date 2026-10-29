using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using Application.AI.Common.Services.Compliance;
using Application.Common.Interfaces.Common;
using Domain.AI.Audit;
using Domain.Common.Config;
using Domain.Common.Config.AI.Audit;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Audit;

/// <summary>
/// Finds the key the audit pseudonyms are made with (#903): the configured <see cref="AuditConfig.PseudonymKey"/>, or the
/// key file at <see cref="AuditConfig.PseudonymKeyPath"/>, made with owner-only access the first time it is needed.
/// </summary>
/// <remarks>
/// <para>
/// A key made per process, as the telemetry pseudonymizer does when it has none, would leave every reference in a
/// write-once trail unattributable after the next restart. A key file kept beside the session data, outside the audit
/// folders, keeps references stable on one machine with nothing to configure; a secret-store key is still what a
/// deployment of several instances needs, and the warning written when a key file is made says so.
/// </para>
/// <para>
/// <b>The file only ever appears complete.</b> The key is written to a temporary file in the same folder, flushed to disk,
/// and moved into place with no overwrite. A crash or a full disk leaves at most a stray temporary file, never a half-written
/// key that would stop every later start; two processes starting together both end up with whichever key was moved first.
/// Access is limited to the account that made it: mode 0600 on Unix, and on Windows an access list naming only that
/// account, not inherited from the folder.
/// </para>
/// </remarks>
internal static class AuditPseudonymKey
{
    /// <summary>The key's length in bytes, made or accepted: the output size of HMAC-SHA256.</summary>
    internal const int KeyBytes = HmacErasureSubjectReference.MinimumKeyBytes;

    private const int ReadAttempts = 5;

    /// <summary>Returns the key, reading or making the key file when none is configured.</summary>
    /// <exception cref="InvalidOperationException">The configured key or the key file's content is not a usable key.</exception>
    internal static byte[] Resolve(AppConfig appConfig, IOwnerOnlyDirectoryCreator directories, ILogger logger)
    {
        var config = appConfig.AI.Audit;
        if (!string.IsNullOrEmpty(config.PseudonymKey))
        {
            if (!HmacErasureSubjectReference.IsUsableKey(config.PseudonymKey))
                throw new InvalidOperationException(
                    $"AppConfig:AI:Audit:PseudonymKey is set but is not base64 of at least {KeyBytes} bytes.");
            return Convert.FromBase64String(config.PseudonymKey);
        }

        var path = Path.GetFullPath(config.PseudonymKeyPath);
        return File.Exists(path) ? Read(path, logger) : Create(path, appConfig, directories, logger);
    }

    private static byte[] Read(string path, ILogger logger)
    {
        WarnIfReadableByOthers(path, logger);
        var content = ReadAllTextWithRetry(path).Trim();
        if (!HmacErasureSubjectReference.IsUsableKey(content))
            throw new InvalidOperationException(
                $"The audit pseudonym key file at {path} is not base64 of at least {KeyBytes} bytes. Restore it from a " +
                "backup rather than deleting it: a new key cannot recompute the references already in the audit trails.");
        return Convert.FromBase64String(content);
    }

    // Another process may hold the file open for the instant it moves it into place.
    private static string ReadAllTextWithRetry(string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return File.ReadAllText(path);
            }
            catch (IOException) when (attempt < ReadAttempts && File.Exists(path))
            {
                Thread.Sleep(50 * attempt);
            }
        }
    }

    private static byte[] Create(string path, AppConfig appConfig, IOwnerOnlyDirectoryCreator directories, ILogger logger)
    {
        var folder = Path.GetDirectoryName(path)!;
        directories.Create(folder, logger);
        var key = RandomNumberGenerator.GetBytes(KeyBytes);
        var temporary = Path.Combine(folder, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            WriteOwnerOnly(temporary, Convert.ToBase64String(key));
            File.Move(temporary, path, overwrite: false);
        }
        catch (IOException) when (File.Exists(path))
        {
            // Another process on this machine moved its key into place first: use theirs, so both write the same references.
            return Read(path, logger);
        }
        finally
        {
            File.Delete(temporary);
        }

        if (AuditTrailsHoldRecords(appConfig))
        {
            // The key file is gone but the trails are not: the references already in them were made under a key this
            // process no longer has, and will not match the ones it makes from now on.
            logger.LogError(
                "Made a new audit pseudonym key at {KeyPath}, but the audit trails already hold records. If those records " +
                "hold references (ref:/fp:) made under an earlier key, they will not match new ones: restore the earlier " +
                "key file from a backup, or set AppConfig:AI:Audit:PseudonymKey to it, and restart. If the trails were " +
                "written before audit pseudonyms existed, nothing was lost; back this key up.",
                path);
            return key;
        }

        logger.LogWarning(
            "Made a new audit pseudonym key at {KeyPath}. Audit trails record people and tool arguments as references " +
            "under it. Back it up, since references cannot be recomputed without it, and where more than one instance " +
            "writes the trails, set the same key in AppConfig:AI:Audit:PseudonymKey from a secret store instead.",
            path);
        return key;
    }

    // Live and archived day-files both count. When the folders cannot be read, assume the worst: the error is the safer
    // message, and this check must never stop the host on its own.
    private static bool AuditTrailsHoldRecords(AppConfig appConfig)
    {
        try
        {
            var archive = appConfig.AI.Audit.ArchivePath;
            return AuditChains.All(appConfig, TimeProvider.System).Any(chain =>
                HoldsRecords(chain.Segments)
                || HoldsHeldRecords(chain.PendingPath)
                || (!string.IsNullOrWhiteSpace(archive) && HoldsRecords(chain.Segments.ArchivedUnder(archive))));
        }
        catch (Exception)
        {
            return true;
        }
    }

    private static bool HoldsRecords(DatedAuditSegments segments) =>
        segments.Ordered().Any(file => new FileInfo(file).Length > 0);

    // A record held for replay (#1001) carries the same references and reaches the trail later.
    private static bool HoldsHeldRecords(string pendingFolder) =>
        Directory.Exists(pendingFolder) && Directory.EnumerateFiles(pendingFolder, "*.pending").Any();

    // A key file restored from a backup or copied by hand can come back readable by anyone on the machine, who could then
    // recompute references from the trails. Starting is still right, since refusing would stop the audit trails too.
    private static void WarnIfReadableByOthers(string path, ILogger logger)
    {
        bool shared;
        try
        {
            shared = OperatingSystem.IsWindows() ? SharedOnWindows(path) : SharedOnUnix(path);
        }
        catch (Exception ex)
        {
            // A share or mount that does not expose permissions: say so, and start anyway.
            logger.LogWarning(ex, "Could not check who can read the audit pseudonym key file at {KeyPath}", path);
            return;
        }

        if (shared)
            logger.LogWarning(
                "The audit pseudonym key file at {KeyPath} is readable by accounts other than this one. Limit it to the " +
                "account the application runs as (mode 0600, or an access list naming only that account).",
                path);
    }

    [UnsupportedOSPlatform("windows")]
    private static bool SharedOnUnix(string path) =>
        (File.GetUnixFileMode(path)
            & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite)) != 0;

    [SupportedOSPlatform("windows")]
    private static bool SharedOnWindows(string path)
    {
        var acl = new FileInfo(path).GetAccessControl();
        var owner = WindowsIdentity.GetCurrent().User;
        // SYSTEM and the local administrators can read any file regardless; granting them changes nothing.
        var trusted = new[]
        {
            owner,
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
        };
        return acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
            .Any(rule => rule.AccessControlType == AccessControlType.Allow && !trusted.Contains(rule.IdentityReference));
    }

    private static void WriteOwnerOnly(string path, string content)
    {
        using var stream = OperatingSystem.IsWindows() ? CreateNewWindows(path) : CreateNewUnix(path);
        using (var writer = new StreamWriter(stream, leaveOpen: true))
            writer.Write(content);
        stream.Flush(flushToDisk: true);
    }

    [UnsupportedOSPlatform("windows")]
    private static FileStream CreateNewUnix(string path) => new(path, new FileStreamOptions
    {
        Mode = FileMode.CreateNew,
        Access = FileAccess.Write,
        Share = FileShare.None,
        UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
    });

    [SupportedOSPlatform("windows")]
    private static FileStream CreateNewWindows(string path)
    {
        var owner = WindowsIdentity.GetCurrent().User!;
        var security = new FileSecurity();
        // An access list given at creation is applied as given, without the folder's inherited entries; marking it
        // protected as well keeps it that way if the file is ever moved or its access list re-applied.
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(owner);
        security.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.FullControl, AccessControlType.Allow));
        return new FileInfo(path).Create(
            FileMode.CreateNew, FileSystemRights.Write | FileSystemRights.ReadData, FileShare.None, 4096, FileOptions.None, security);
    }
}
