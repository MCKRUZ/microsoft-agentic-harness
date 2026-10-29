using FluentAssertions;
using Xunit;

namespace Presentation.Common.Tests.Composition;

/// <summary>
/// The compliance trail is kept for good by design, so a test composition that wrote it to its default folder would leave
/// a permanent, growing chain in the build output and run every host's replay service on it (#995). Both ways tests reach
/// a host's configuration point it at this run's own state root instead.
/// </summary>
public sealed class ComplianceStateIsIsolatedTests
{
    public static TheoryData<string> AuditStateSettings() =>
    [
        "AppConfig:AI:Erasure:AuditStoragePath",
        "AppConfig:AI:Audit:ReceiptPath",
        "AppConfig:AI:Audit:PseudonymKeyPath",
    ];

    [Theory]
    [MemberData(nameof(AuditStateSettings))]
    public void ComposeServices_NoSettingChosen_PutsTheAuditStateUnderThisRunsRoot(string key)
    {
        var (_, configuration) = CompositionRootTestHost.ComposeServices([]);

        configuration[key].Should().StartWith(IsolatedStateRoot.Root, "nothing kept for good may land in the build output");
    }

    [Theory]
    [MemberData(nameof(AuditStateSettings))]
    public void Redirect_ForHostsBuiltFromEnvironment_PutsTheAuditStateUnderThisRunsRoot(string key)
    {
        Environment.GetEnvironmentVariable(key.Replace(":", "__", StringComparison.Ordinal))
            .Should().StartWith(IsolatedStateRoot.Root, "a web host reads these from the environment TestStateRoot sets");
    }
}
