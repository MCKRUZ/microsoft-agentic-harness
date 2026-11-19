using Application.Core.Validation;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.Audit;
using FluentAssertions;
using Infrastructure.AI.Audit;
using Xunit;

namespace Infrastructure.AI.Tests.Audit;

/// <summary>
/// <see cref="AuditChains.All"/> and the archive-path validator's list of live audit folders are kept by hand in two
/// projects. A chain missing from the validator's list could be archived into its own live folder, and its key-file check
/// would not cover it either (#969, the sixth chain).
/// </summary>
public sealed class AuditChainsTests
{
    [Fact]
    public void All_EveryDefaultChainsFolder_IsOneTheArchivePathMayNotOverlap()
    {
        var chains = AuditChains.All(new AppConfig(), TimeProvider.System);

        chains.Select(c => c.Name).Should().Contain("compliance");
        var unprotected = chains
            .Where(chain => new AuditArchivePathValidator()
                .Validate(new AIConfig { Audit = new AuditConfig { ArchivePath = chain.Segments.Folder } }).IsValid)
            .Select(chain => chain.Name);
        unprotected.Should().BeEmpty("every audit chain's folder must be on AuditArchivePathValidator.LiveFolders");
    }

    [Fact]
    public void Compliance_DayFilesSitInAFixedFolderInsideTheConfiguredPath()
    {
        var config = new AppConfig { AI = new AIConfig { Erasure = new ErasureConfig { AuditStoragePath = "trail-root" } } };

        var chain = AuditChains.Compliance(config, TimeProvider.System);

        chain.Segments.Folder.Should().Be(Path.Combine("trail-root", "compliance"),
            "the archive folder is named after it, so it must not take its name from configuration");
        chain.Segments.LegacyFile.Should().BeNull();
    }
}
