using System.Text.RegularExpressions;
using Domain.Common.Compliance;
using FluentAssertions;
using Tests.Common;
using Xunit;

namespace Presentation.ExecutionApi.Tests;

/// <summary>
/// The published contract lists every status a receipt line or a manifest line can carry (#1003). The spec is written by
/// hand, so a status added to either enum and not to the spec would reach clients as a value the contract says cannot
/// occur; this fails instead.
/// </summary>
public sealed class ComplianceContractStatusesTests
{
    private static readonly Regex EnumList = new(@"enum:\s*\[(?<values>[^\]]*)\]", RegexOptions.CultureInvariant);

    private static IReadOnlyList<string[]> SpecEnums()
    {
        var spec = File.ReadAllText(RepoRoot.Combine(
            "documentation", "onboarding", "assets", "openapi", "bundle-api.yaml"));
        return EnumList.Matches(spec)
            .Select(m => m.Groups["values"].Value
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToList();
    }

    [Fact]
    public void TheReceiptLineStatuses_InTheSpec_AreEveryStoreErasureStatus()
    {
        SpecEnums().Should().ContainSingle(values => values.Contains(nameof(StoreErasureStatus.Erased)))
            .Which.Should().BeEquivalentTo(System.Enum.GetNames<StoreErasureStatus>());
    }

    [Fact]
    public void TheManifestLineStatuses_InTheSpec_AreEveryExportStatus()
    {
        SpecEnums().Should().ContainSingle(values => values.Contains(nameof(ExportStatus.Included)))
            .Which.Should().BeEquivalentTo(System.Enum.GetNames<ExportStatus>());
    }
}
