using Application.AI.Common.Interfaces.Tools;
using Application.AI.Common.Services.Tools;
using Domain.AI.Changes;
using Domain.AI.Governance;
using Domain.AI.Models;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Application.AI.Common.Tests.Services.Tools;

/// <summary>
/// Tests for <see cref="ToolBehaviorRegistry"/> — what a tool declared about itself, from whichever of
/// the two sources knows: an external server's advertisement, or the tool's own registration.
/// </summary>
public sealed class ToolBehaviorRegistryTests
{
    private static ToolBehaviorRegistry CreateRegistry(params ITool[] tools)
    {
        var services = new ServiceCollection();
        foreach (var tool in tools)
            services.AddKeyedSingleton<ITool>(tool.Name, tool);

        return new ToolBehaviorRegistry(services.BuildServiceProvider());
    }

    [Fact]
    public void Resolve_NameNobodyHasDescribed_IsUnknown()
    {
        var sut = CreateRegistry();

        sut.Resolve("never_heard_of_it").Should().Be(ToolBehavior.Unknown);
    }

    [Fact]
    public void Resolve_RegisteredTool_ReadsItsOwnDeclaration()
    {
        var sut = CreateRegistry(
            new FakeTool("lookup", isReadOnly: true),
            new FakeTool("deploy", isReadOnly: false));

        sut.Resolve("lookup").Should().BeEquivalentTo(
            new ToolBehavior(ToolBehaviorSource.FirstParty, ReadOnly: true));
        sut.Resolve("lookup").IsExemptFromApproval.Should().BeTrue();
        sut.Resolve("deploy").IsExemptFromApproval.Should().BeFalse();
    }

    [Fact]
    public void Resolve_AdvertisedTool_KeepsWhatTheServerSaidAndWhoSaidIt()
    {
        var sut = CreateRegistry();
        sut.RecordAdvertised(
            "search_pages",
            new ToolBehavior(ToolBehaviorSource.TrustedMcpServer, ReadOnly: true, OpenWorld: true));

        var resolved = sut.Resolve("search_pages");

        resolved.Source.Should().Be(ToolBehaviorSource.TrustedMcpServer);
        resolved.ReadOnly.Should().BeTrue();
        resolved.OpenWorld.Should().BeTrue();
        resolved.IsExemptFromApproval.Should().BeTrue();
    }

    [Fact]
    public void Resolve_IsCaseInsensitive_BecauseToolNamesAreMatchedThatWayEverywhereElse()
    {
        var sut = CreateRegistry();
        sut.RecordAdvertised("Search_Pages", new ToolBehavior(ToolBehaviorSource.TrustedMcpServer, ReadOnly: true));

        sut.Resolve("search_pages").IsExemptFromApproval.Should().BeTrue();
    }

    [Fact]
    public void RecordAdvertised_ADifferentServerClaimingTheSameName_CannotLoosenIt()
    {
        // The shadowing attack: a hostile server advertises a name a stricter server already claimed
        // and marks it read-only, hoping the later write wins. Stricter must survive regardless of
        // arrival order, so both orders are asserted.
        //
        // The two records carry DIFFERENT server names, which is load-bearing: with the same name this
        // is a server re-reporting its own tool, which must be believed. An earlier version of this
        // test left both unattributed and therefore proved the weaker of the two rules under the
        // stronger one's name.
        var sut = CreateRegistry();

        sut.RecordAdvertised("write_page", Advertised("honest-server", ReadOnly: false));
        sut.RecordAdvertised("write_page", Advertised("shadowing-server", ReadOnly: true));

        sut.Resolve("write_page").IsExemptFromApproval.Should().BeFalse();
        sut.Resolve("write_page").ServerName.Should().Be("honest-server");

        var reversed = CreateRegistry();
        reversed.RecordAdvertised("write_page", Advertised("shadowing-server", ReadOnly: true));
        reversed.RecordAdvertised("write_page", Advertised("honest-server", ReadOnly: false));

        reversed.Resolve("write_page").IsExemptFromApproval.Should().BeFalse();
    }

    [Fact]
    public void RecordAdvertised_AServerRevisingItsOwnTool_IsBelievedInBothDirections()
    {
        // Two things ride on this, and an earlier version of the registry got the second one wrong.
        //
        // Tightening is the rug-pull case: a server that advertised a clean read-only tool and later
        // advertises it as destructive must be believed the second time.
        //
        // Loosening is how an operator's decision takes effect. A server discovered before it was
        // marked trusted has a non-exempt record on file; granting trust re-records the same tools from
        // the same server as trusted. A rule that kept the stricter entry unconditionally would pin the
        // old record forever, and the config change would silently do nothing until a restart.
        var tightening = CreateRegistry();
        tightening.RecordAdvertised("notes", Advertised("notion", ReadOnly: true, trusted: true));
        tightening.Resolve("notes").IsExemptFromApproval.Should().BeTrue();

        tightening.RecordAdvertised("notes", Advertised("notion", Destructive: true, trusted: true));
        tightening.Resolve("notes").IsExemptFromApproval.Should().BeFalse();

        var loosening = CreateRegistry();
        loosening.RecordAdvertised("notes", Advertised("notion", ReadOnly: true));
        loosening.Resolve("notes").IsExemptFromApproval.Should().BeFalse();

        loosening.RecordAdvertised("notes", Advertised("notion", ReadOnly: true, trusted: true));
        loosening.Resolve("notes").IsExemptFromApproval.Should().BeTrue();
    }

    [Fact]
    public void RecordAdvertised_AnUnattributedRecord_CannotOverwriteAnAttributedOne()
    {
        // "Same source" needs a source. Two records with no server name are not thereby the same
        // server, and treating them as one would make the unattributed path the loosest in the system.
        var sut = CreateRegistry();

        sut.RecordAdvertised("notes", Advertised("notion", ReadOnly: false));
        sut.RecordAdvertised("notes", new ToolBehavior(ToolBehaviorSource.TrustedMcpServer, ReadOnly: true));

        sut.Resolve("notes").IsExemptFromApproval.Should().BeFalse();
    }

    [Fact]
    public void Resolve_NameKnownToBothSources_AnswersWithWhicheverDoesNotExempt()
    {
        // A declared tool is resolved from MCP before keyed DI, so which implementation actually runs
        // depends on whether discovery succeeded. A rule that changed with it would be worse than one
        // that always answers with the stricter of the two — asserted in both directions.
        var advertisedWriter = CreateRegistry(new FakeTool("notes", isReadOnly: true));
        advertisedWriter.RecordAdvertised("notes", new ToolBehavior(ToolBehaviorSource.TrustedMcpServer, ReadOnly: false));

        advertisedWriter.Resolve("notes").IsExemptFromApproval.Should().BeFalse();

        var registeredWriter = CreateRegistry(new FakeTool("notes", isReadOnly: false));
        registeredWriter.RecordAdvertised("notes", new ToolBehavior(ToolBehaviorSource.TrustedMcpServer, ReadOnly: true));

        registeredWriter.Resolve("notes").IsExemptFromApproval.Should().BeFalse();
    }

    [Fact]
    public void Resolve_ReadOnlyFromAnUntrustedServer_StaysNonExemptEvenWhenTheToolIsAlsoRegisteredLocally()
    {
        // Both halves say read-only, but one of them is not entitled to. The registry must not let the
        // trusted half launder the untrusted one — the answer is the untrusted declaration.
        var sut = CreateRegistry(new FakeTool("notes", isReadOnly: true));
        sut.RecordAdvertised("notes", new ToolBehavior(ToolBehaviorSource.UntrustedMcpServer, ReadOnly: true));

        sut.Resolve("notes").IsExemptFromApproval.Should().BeFalse();
        sut.Resolve("notes").Source.Should().Be(ToolBehaviorSource.UntrustedMcpServer);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankNames_AreNeitherRecordedNorResolved(string name)
    {
        var sut = CreateRegistry();

        sut.RecordAdvertised(name, new ToolBehavior(ToolBehaviorSource.TrustedMcpServer, ReadOnly: true));

        sut.Resolve(name).Should().Be(ToolBehavior.Unknown);
    }

    /// <summary>A declaration as an MCP server would make it, attributed to that server.</summary>
    private static ToolBehavior Advertised(
        string serverName, bool? ReadOnly = null, bool? Destructive = null, bool trusted = false) =>
        new(trusted ? ToolBehaviorSource.TrustedMcpServer : ToolBehaviorSource.UntrustedMcpServer,
            ReadOnly: ReadOnly,
            Destructive: Destructive,
            ServerName: serverName);

    private sealed class FakeTool(string name, bool isReadOnly) : ITool
    {
        public string Name => name;
        public string Description => "fake tool";
        public IReadOnlyList<string> SupportedOperations => [];
        public bool IsReadOnly => isReadOnly;
        public BlastRadius RiskTier => BlastRadius.Medium;

        public Task<ToolResult> ExecuteAsync(
            string operation,
            IReadOnlyDictionary<string, object?> parameters,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Not used in registry tests.");
    }
}
