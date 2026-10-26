using Application.Core.Validation;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.Audit;
using Domain.Common.Config.AI.Governance;
using FluentAssertions;
using Xunit;

namespace Application.Core.Tests.Validation;

/// <summary>
/// The audit pseudonym key settings (#903): a configured key must be usable, and the key file must not sit where the
/// records it protects are kept or copied, or it would travel with them into the archive.
/// </summary>
public sealed class AuditPseudonymKeyValidatorTests
{
    private readonly AuditPseudonymKeyValidator _validator = new();

    private static AIConfig Config(string key = "", string? keyPath = null, string archive = "") => new()
    {
        Governance = new GovernanceConfig
        {
            AuditStoragePath = Path.Combine("audit-root", "governance"),
            Escalation = new EscalationConfig { AuditStoragePath = Path.Combine("audit-root", "escalations") },
        },
        Audit = new AuditConfig
        {
            PseudonymKey = key,
            PseudonymKeyPath = keyPath ?? Path.Combine(".agent-sessions", "keys", "audit-pseudonym.key"),
            ArchivePath = archive,
        },
    };

    [Fact]
    public void Validate_Defaults_AreValid()
    {
        _validator.Validate(new AIConfig()).IsValid.Should().BeTrue("a host that sets nothing must keep booting");
    }

    [Fact]
    public void Validate_UsableConfiguredKey_IsValid()
    {
        var key = Convert.ToBase64String(new byte[32]);

        _validator.Validate(Config(key)).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("not base64 at all!")]
    [InlineData("AAECAwQFBgcICQoLDA0ODw==")] // 16 bytes
    public void Validate_UnusableConfiguredKey_FailsNamingTheSetting(string key)
    {
        var result = _validator.Validate(Config(key));

        result.Errors.Should().ContainSingle().Which.ErrorMessage.Should().Contain("PseudonymKey").And.Contain("32 bytes");
    }

    [Theory]
    [InlineData("governance")]
    [InlineData("escalations")]
    public void Validate_KeyFileInsideAnAuditLogFolder_Fails(string folder)
    {
        var result = _validator.Validate(Config(keyPath: Path.Combine("audit-root", folder, "audit.key")));

        result.Errors.Should().ContainSingle().Which.ErrorMessage.Should().Contain("outside the audit folders");
    }

    [Fact]
    public void Validate_KeyFileInsideTheArchive_Fails()
    {
        var result = _validator.Validate(Config(keyPath: Path.Combine("archive", "keys", "audit.key"), archive: "archive"));

        result.Errors.Should().ContainSingle().Which.ErrorMessage.Should().Contain("Audit:ArchivePath");
    }

    [Fact]
    public void Validate_KeyFileBesideTheAuditFolders_IsValid()
    {
        // A sibling of the log folders is outside them: the default sits in .agent-sessions/keys next to
        // .agent-sessions/escalations and the rest.
        _validator.Validate(Config(keyPath: Path.Combine("audit-root", "keys", "audit.key"))).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_ConfiguredKey_IgnoresTheKeyFileLocation()
    {
        var key = Convert.ToBase64String(new byte[32]);

        _validator.Validate(Config(key, keyPath: Path.Combine("audit-root", "governance", "audit.key")))
            .IsValid.Should().BeTrue("with a configured key no key file is read or made");
    }
}
