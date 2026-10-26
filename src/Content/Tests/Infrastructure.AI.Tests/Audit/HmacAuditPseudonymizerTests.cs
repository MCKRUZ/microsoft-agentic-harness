using System.Security.Cryptography;
using System.Text;
using Application.AI.Common.Interfaces.Audit;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.Audit;
using FluentAssertions;
using Infrastructure.AI.Audit;
using Infrastructure.AI.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Audit;

/// <summary>
/// The audit pseudonymizer (#903): the audit trails are kept for good, so a person or a tool argument is recorded as a
/// keyed reference that still means the same thing after a restart, carries the id of its key, and cannot be reversed or
/// confirmed by anyone without the key.
/// </summary>
public sealed class HmacAuditPseudonymizerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"audit-key-tests-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private HmacAuditPseudonymizer FromFile(string? keyPath = null, ILogger<HmacAuditPseudonymizer>? logger = null) => new(
        Options.Create(new AppConfig
        {
            AI = new AIConfig { Audit = new AuditConfig { PseudonymKeyPath = keyPath ?? Path.Combine(_dir, "keys", "audit.key") } },
        }),
        new OwnerOnlyDirectoryCreator(),
        logger ?? Mock.Of<ILogger<HmacAuditPseudonymizer>>());

    [Fact]
    public void Person_ConfiguredKey_IsAKeyedReferenceTaggedWithTheKeyId()
    {
        var pseudonymizer = AuditPseudonyms.Create();

        var reference = pseudonymizer.Person("alice@contoso.com");

        var prk = HKDF.Extract(
            HashAlgorithmName.SHA256,
            Convert.FromBase64String(AuditPseudonyms.TestKey),
            "agentic-harness-audit-pseudonym-v1"u8.ToArray());
        var personKey = HKDF.Expand(HashAlgorithmName.SHA256, prk, 32, "audit-person"u8.ToArray());
        var expected = Convert.ToHexStringLower(HMACSHA256.HashData(personKey, Encoding.UTF8.GetBytes("ALICE@CONTOSO.COM")));
        reference.Should().Be($"ref:{pseudonymizer.KeyId}:{expected}");
        pseudonymizer.KeyId.Should().MatchRegex("^[0-9a-f]{8}$");
        reference.Should().NotContain("alice").And.NotContain(
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("alice@contoso.com"))),
            "a plain hash of a name can be confirmed by hashing a guess");
    }

    [Fact]
    public void Person_IgnoresCase_LikeTheApproverRoster()
    {
        // Approver names are compared ignoring case, so one person signing in as "Alice@Corp.com" and as
        // "alice@corp.com" must get one reference, or a key holder's lookup misses half their approvals.
        var pseudonymizer = AuditPseudonyms.Create();

        pseudonymizer.Person("Alice@Corp.com").Should().Be(pseudonymizer.Person("alice@corp.com"));
        pseudonymizer.Person("alice").Should().NotBe(pseudonymizer.Person("bob"));
    }

    [Fact]
    public void Person_AndFingerprints_OfTheSameText_NeverCoincide()
    {
        // An argument that happens to equal an approver's name must not join to that approver's reference.
        var pseudonymizer = AuditPseudonyms.Create();

        var hashes = new[]
        {
            pseudonymizer.Person("alice"),
            pseudonymizer.Fingerprint(IAuditPseudonymizer.Purposes.ToolArgument, "alice"),
            pseudonymizer.Fingerprint(IAuditPseudonymizer.Purposes.EgressTarget, "alice"),
        }.Select(h => h[(h.LastIndexOf(':') + 1)..]);

        hashes.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void KeyId_DiffersBetweenKeys_SoARotatedKeysReferencesReadAsSuch()
    {
        var other = Convert.ToBase64String(Enumerable.Range(100, 32).Select(i => (byte)i).ToArray());

        AuditPseudonyms.Create().KeyId.Should().NotBe(AuditPseudonyms.Create(other).KeyId);
    }

    [Fact]
    public void NoConfiguredKey_MakesAKeyFileOnce_AndEveryLaterInstanceReadsTheSameKey()
    {
        // The write-once trail needs the same reference for the same person after a restart, which a key made per process
        // cannot give.
        var logger = new Mock<ILogger<HmacAuditPseudonymizer>>();

        var first = FromFile(logger: logger.Object);
        var afterRestart = FromFile();

        afterRestart.Person("alice").Should().Be(first.Person("alice"));
        afterRestart.KeyId.Should().Be(first.KeyId);
        File.Exists(Path.Combine(_dir, "keys", "audit.key")).Should().BeTrue();
        logger.Invocations.Count(i => (LogLevel)i.Arguments[0] == LogLevel.Warning)
            .Should().Be(1, "making a key file is announced, with what to do about it");
    }

    [Fact]
    public void NoConfiguredKey_KeyFileOnPosix_IsReadableByTheOwnerOnly()
    {
        if (OperatingSystem.IsWindows())
            return; // Windows has no POSIX mode; the folder's ACL comes from OwnerOnlyDirectoryCreator.

        _ = FromFile();

        File.GetUnixFileMode(Path.Combine(_dir, "keys", "audit.key"))
            .Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [Fact]
    public void NoConfiguredKey_KeyFileOnWindows_GrantsOnlyTheAccountThatMadeIt()
    {
        if (!OperatingSystem.IsWindows())
            return;

        _ = FromFile();

        var acl = new FileInfo(Path.Combine(_dir, "keys", "audit.key")).GetAccessControl();
        acl.AreAccessRulesProtected.Should().BeTrue("the folder's inherited access, often all local users, must not apply");
        var rules = acl.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier))
            .Cast<System.Security.AccessControl.FileSystemAccessRule>().ToList();
        rules.Should().ContainSingle().Which.IdentityReference.Should()
            .Be(System.Security.Principal.WindowsIdentity.GetCurrent().User);
    }

    [Fact]
    public void NoConfiguredKey_StrayTemporaryFileFromACrash_DoesNotBlockStartup()
    {
        // The key is written to a temporary file and moved into place, so a crash leaves only a temporary file behind,
        // never a half-written key file that would stop every later start.
        var keys = Path.Combine(_dir, "keys");
        Directory.CreateDirectory(keys);
        File.WriteAllText(Path.Combine(keys, ".audit.key.0123.tmp"), "");

        var pseudonymizer = FromFile();

        FromFile().Person("alice").Should().Be(pseudonymizer.Person("alice"));
        Directory.GetFiles(keys, "*.tmp").Should().ContainSingle("only the stray one; its own temporary file is gone");
    }

    [Fact]
    public void KeyFileWithUnusableContent_Throws_RatherThanReplacingIt()
    {
        // Replacing it would silently start new references that match nothing already in the trails.
        var path = Path.Combine(_dir, "audit.key");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(path, "short");

        var act = () => FromFile(path);

        act.Should().Throw<InvalidOperationException>().WithMessage("*Restore it from a backup*");
        File.ReadAllText(path).Should().Be("short");
    }

    [Theory]
    [InlineData("not base64 at all!")]
    [InlineData("AAECAwQFBgcICQoLDA0ODw==")] // 16 bytes
    public void ConfiguredKeyUnusable_Throws(string key)
    {
        var act = () => AuditPseudonyms.Create(key);

        act.Should().Throw<InvalidOperationException>().WithMessage("*PseudonymKey*32 bytes*");
    }
}
