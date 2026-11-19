using Application.Core.Validation;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.Audit;
using FluentAssertions;
using Xunit;

namespace Application.Core.Tests.Validation;

/// <summary>
/// #416: the audit archive is read with the live logs as one chain, so it must be a folder of its own.
/// </summary>
public class AuditArchivePathValidatorTests
{
    private readonly AuditArchivePathValidator _validator = new();

    private static AIConfig WithArchive(string archivePath) => new()
    {
        Audit = new AuditConfig { ArchivePath = archivePath },
    };

    [Fact]
    public void NoArchive_IsValid()
    {
        _validator.Validate(WithArchive(string.Empty)).IsValid.Should().BeTrue();
    }

    [Fact]
    public void AnArchiveOfItsOwn_IsValid()
    {
        _validator.Validate(WithArchive(Path.Combine("mnt", "audit-archive"))).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData(".agent-sessions/governance", "Governance:AuditStoragePath")]                    // the same folder
    [InlineData(".agent-sessions/governance/archive", "Governance:AuditStoragePath")]            // inside it
    [InlineData(".agent-sessions", "Governance:AuditStoragePath")]                               // around it
    [InlineData(".agent-sessions/escalations/", "Governance:Escalation:AuditStoragePath")]       // trailing separator
    [InlineData(".agent-sessions/changes", "Changes:AuditStoragePath")]
    [InlineData(".agent-sessions/egress", "Egress:AuditStoragePath")]
    [InlineData("data/audit/drift-audit", "DriftDetection:AuditPath")]
    [InlineData(".agent-sessions/compliance", "Erasure:AuditStoragePath")]
    [InlineData(".agent-sessions/compliance-pending/archive", "Erasure:AuditStoragePath")]
    // Every trail's pending folder (#1001) sits inside the folder its trail is configured in, so it is refused with it.
    [InlineData(".agent-sessions/governance/governance-pending", "Governance:AuditStoragePath")]
    [InlineData(".agent-sessions/escalations/escalations-pending", "Governance:Escalation:AuditStoragePath")]
    [InlineData(".agent-sessions/changes/changes-pending", "Changes:AuditStoragePath")]
    [InlineData(".agent-sessions/egress/egress-pending", "Egress:AuditStoragePath")]
    [InlineData("data/audit/drift-audit-pending", "DriftDetection:AuditPath")]
    public void AnArchiveOverlappingALogFolder_IsRefused_NamingTheFolder(string archivePath, string overlapped)
    {
        var result = _validator.Validate(WithArchive(archivePath));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.ErrorMessage.Should().Contain(overlapped);
    }

    [Fact]
    public void AnArchiveInsideTheCheckpointFolder_IsRefused()
    {
        var config = new AIConfig
        {
            Audit = new AuditConfig { ArchivePath = Path.Combine("receipts", "archive"), ReceiptPath = "receipts" },
        };

        var result = _validator.Validate(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.ErrorMessage.Should().Contain("Audit:ReceiptPath");
    }

    [Fact]
    public void AFolderThatOnlySharesANamePrefix_IsNotAnOverlap()
    {
        // ".agent-sessions/governance-archive" is beside the governance folder, not inside it.
        _validator.Validate(WithArchive(".agent-sessions/governance-archive")).IsValid.Should().BeTrue();
    }
}
