using Application.AI.Common.Interfaces.Tools;
using Application.AI.Common.Services.Tools;
using Domain.AI.Bundles;
using Domain.AI.Changes;
using Domain.AI.Models;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Application.AI.Common.Tests.Services.Tools;

/// <summary>
/// Tests for <see cref="ToolCatalog"/> — enumerating the host's keyed tools and answering only for
/// those the caller's envelope grants.
/// </summary>
/// <remarks>
/// The filtering assertions here are the point of the type, not incidental coverage. An unfiltered
/// catalog discloses the host's whole tool inventory to any authenticated caller, and the
/// <c>FindGranted</c> tests specifically pin that a registered-but-ungranted tool is reported
/// identically to one that does not exist — the difference between the two is what a caller would
/// use to map the inventory one name at a time.
/// </remarks>
public sealed class ToolCatalogTests
{
    private static IToolCatalog CreateCatalog(params ITool[] tools)
    {
        var services = new ServiceCollection();
        foreach (var tool in tools)
            services.AddKeyedSingleton<ITool>(tool.Name, tool);

        return new ToolCatalog(
            services.BuildServiceProvider(),
            tools.Select(tool => tool.Name),
            NullLogger<ToolCatalog>.Instance);
    }

    private static CapabilityEnvelope Granting(params string[] toolNames) =>
        new() { AllowedTools = toolNames };

    [Fact]
    public void ListGranted_ReturnsOnlyToolsTheEnvelopeGrants()
    {
        var sut = CreateCatalog(
            new FakeTool("alpha"),
            new FakeTool("beta"),
            new FakeTool("gamma"));

        var listed = sut.ListGranted(Granting("alpha", "gamma"));

        listed.Select(entry => entry.Name).Should().Equal("alpha", "gamma");
    }

    [Fact]
    public void ListGranted_EnvelopeGrantingNothing_ReturnsEmpty()
    {
        // The shipped default envelope grants no tools. A caller under it must see an empty
        // catalog, never the full registration list.
        var sut = CreateCatalog(new FakeTool("alpha"), new FakeTool("beta"));

        sut.ListGranted(new CapabilityEnvelope()).Should().BeEmpty();
    }

    [Fact]
    public void ListGranted_OrdersByNameSoTheListingIsStable()
    {
        var sut = CreateCatalog(
            new FakeTool("zulu"),
            new FakeTool("alpha"),
            new FakeTool("mike"));

        var listed = sut.ListGranted(Granting("zulu", "alpha", "mike"));

        listed.Select(entry => entry.Name).Should().Equal("alpha", "mike", "zulu");
    }

    [Fact]
    public void ListGranted_MatchesGrantsCaseInsensitively()
    {
        // CapabilityEnvelope.GrantsTool is case-insensitive; a catalog that filtered
        // case-sensitively would hide tools the governor would happily let the caller invoke.
        var sut = CreateCatalog(new FakeTool("file_system"));

        sut.ListGranted(Granting("FILE_SYSTEM")).Should().ContainSingle()
            .Which.Name.Should().Be("file_system");
    }

    [Fact]
    public void ListGranted_CarriesTheToolsOwnMetadata()
    {
        var sut = CreateCatalog(new FakeTool(
            "deploy",
            description: "Ships a release.",
            operations: ["plan", "apply"],
            risk: BlastRadius.Critical,
            isReadOnly: false,
            isConcurrencySafe: false));

        var entry = sut.ListGranted(Granting("deploy")).Should().ContainSingle().Subject;

        entry.Description.Should().Be("Ships a release.");
        entry.SupportedOperations.Should().Equal("plan", "apply");
        entry.Risk.Radius.Should().Be(BlastRadius.Critical);
        entry.Risk.IsReadOnly.Should().BeFalse();
        entry.IsConcurrencySafe.Should().BeFalse();
    }

    [Fact]
    public void FindGranted_RegisteredButNotGranted_IsIndistinguishableFromAbsent()
    {
        // The disclosure this whole type exists to prevent: answering differently for
        // "exists but you may not have it" would let any authenticated caller enumerate the
        // host's inventory one name at a time.
        var sut = CreateCatalog(new FakeTool("secret_tool"), new FakeTool("granted_tool"));

        var ungranted = sut.FindGranted("secret_tool", Granting("granted_tool"));
        var absent = sut.FindGranted("no_such_tool", Granting("granted_tool"));

        ungranted.Should().BeNull();
        absent.Should().BeNull();
    }

    [Fact]
    public void FindGranted_GrantedTool_ReturnsIt()
    {
        var sut = CreateCatalog(new FakeTool("alpha"), new FakeTool("beta"));

        sut.FindGranted("beta", Granting("alpha", "beta"))!.Name.Should().Be("beta");
    }

    [Fact]
    public void FindGranted_MatchesNameCaseInsensitively()
    {
        var sut = CreateCatalog(new FakeTool("file_system"));

        sut.FindGranted("File_System", Granting("file_system"))!.Name.Should().Be("file_system");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void FindGranted_BlankName_ReturnsNull(string name)
    {
        var sut = CreateCatalog(new FakeTool("alpha"));

        sut.FindGranted(name, Granting("alpha")).Should().BeNull();
    }

    [Fact]
    public void Catalog_TwoRegistrationsUnderOneKey_DescribesTheOneKeyedResolutionAnswersWith()
    {
        // Keyed DI answers GetKeyedService with the LAST registration for a key. A catalog that
        // described the first would advertise metadata for a tool the caller can never reach, and one
        // that listed both would advertise a tool twice.
        var services = new ServiceCollection();
        services.AddKeyedSingleton<ITool>("dup", new FakeTool("dup", description: "first"));
        services.AddKeyedSingleton<ITool>("dup", new FakeTool("dup", description: "second"));

        var provider = services.BuildServiceProvider();
        var sut = new ToolCatalog(provider, ["dup", "dup"], NullLogger<ToolCatalog>.Instance);

        var listed = sut.ListGranted(Granting("dup"));

        listed.Should().ContainSingle().Which.Description.Should()
            .Be(provider.GetRequiredKeyedService<ITool>("dup").Description);
        listed[0].Description.Should().Be("second");
    }

    [Fact]
    public void Catalog_ToolTheHostCannotConstruct_IsOmittedInsteadOfTakingTheCatalogDown()
    {
        // Not hypothetical: dashboard_control and the render_* tools require an IClientToolBridge
        // that only AgentHub registers, so in every other host they are registered but unbuildable.
        // Resolving them in one bulk pass made a single such tool fail the entire listing — which is
        // how this was found. A tool that cannot be constructed is not invocable, so omitting it is
        // the correct answer; the failure is logged, not swallowed silently.
        var services = new ServiceCollection();
        services.AddKeyedSingleton<ITool>("healthy", new FakeTool("healthy"));
        services.AddKeyedSingleton<ITool>("unbuildable", (_, _) =>
            throw new InvalidOperationException("dependency not registered in this host"));

        var sut = new ToolCatalog(
            services.BuildServiceProvider(),
            ["healthy", "unbuildable"],
            NullLogger<ToolCatalog>.Instance);

        sut.ListGranted(Granting("healthy", "unbuildable"))
            .Select(entry => entry.Name).Should().Equal("healthy");
    }

    [Fact]
    public void Catalog_ToolWhoseNameDisagreesWithItsKey_IsAdvertisedUnderTheKeyThatResolves()
    {
        // The harness resolves tools by name, so the name a caller is given must be the one
        // GetKeyedService answers to. Advertising the tool's self-reported name would publish an
        // identifier that resolves to nothing — a 404 on a tool the catalog just listed.
        var services = new ServiceCollection();
        services.AddKeyedSingleton<ITool>("registered_key", new FakeTool("self_reported_name"));

        var sut = new ToolCatalog(
            services.BuildServiceProvider(),
            ["registered_key"],
            NullLogger<ToolCatalog>.Instance);

        sut.ListGranted(Granting("registered_key")).Should().ContainSingle()
            .Which.Name.Should().Be("registered_key");
    }

    [Fact]
    public void Catalog_ConstructsOnlyTheToolsTheEnvelopeGrants()
    {
        // Work must be proportional to the grant, not to the host. The shipped default envelope
        // grants nothing, so "caller granted nothing" is the common case — and projecting the whole
        // host on its behalf would do every tool's construction cost for a caller not permitted to
        // invoke any of it. Counting constructions is the only way to assert this; a test that only
        // checked the returned list would pass just as happily against eager projection.
        var constructed = new List<string>();
        var services = new ServiceCollection();
        foreach (var name in new[] { "alpha", "beta", "gamma" })
        {
            var captured = name;
            services.AddKeyedSingleton<ITool>(captured, (_, _) =>
            {
                constructed.Add(captured);
                return new FakeTool(captured);
            });
        }

        var sut = new ToolCatalog(
            services.BuildServiceProvider(),
            ["alpha", "beta", "gamma"],
            NullLogger<ToolCatalog>.Instance);

        constructed.Should().BeEmpty("building the catalog must not construct anything");

        sut.ListGranted(new CapabilityEnvelope()).Should().BeEmpty();
        constructed.Should().BeEmpty("a caller granted nothing must construct nothing");

        sut.ListGranted(Granting("beta")).Should().ContainSingle();
        constructed.Should().Equal(["beta"], "only the granted tool may be constructed");

        sut.ListGranted(Granting("beta"));
        constructed.Should().Equal(["beta"], "a projection is cached, not recomputed per request");
    }

    [Fact]
    public void ListGranted_NullEnvelope_Throws()
    {
        var sut = CreateCatalog(new FakeTool("alpha"));

        var act = () => sut.ListGranted(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void A_tool_that_is_not_directly_invocable_is_still_listed()
    {
        // Deliberately listed rather than filtered out. The render_* family and delegate_task are
        // unusable over HTTP but perfectly usable from a workflow's ToolUse step, and hiding them
        // would leave a workflow author unable to discover a tool they are entitled to name.
        var sut = CreateCatalog(new FakeTool("alpha", isDirectlyInvocable: false));

        sut.ListGranted(Granting("alpha")).Should().ContainSingle();
    }

    [Fact]
    public void A_tool_that_is_not_directly_invocable_is_flagged_as_such()
    {
        // The other half: listed, but honestly. Reporting it as invocable would send a caller into a
        // 404 they had no way to anticipate from the catalog they were told to author against.
        var sut = CreateCatalog(new FakeTool("alpha", isDirectlyInvocable: false));

        sut.FindGranted("alpha", Granting("alpha"))!.IsDirectlyInvocable.Should().BeFalse();
    }

    [Fact]
    public void An_ordinary_tool_is_reported_as_directly_invocable()
    {
        // The companion assertion — without it, a catalog that reported every tool as non-invocable
        // would satisfy the test above while making the whole surface unreachable.
        var sut = CreateCatalog(new FakeTool("alpha"));

        sut.FindGranted("alpha", Granting("alpha"))!.IsDirectlyInvocable.Should().BeTrue();
    }

    private sealed class FakeTool(
        string name,
        string description = "fake tool",
        IReadOnlyList<string>? operations = null,
        BlastRadius risk = BlastRadius.Medium,
        bool isReadOnly = false,
        bool isConcurrencySafe = false,
        bool isDirectlyInvocable = true) : ITool
    {
        public string Name => name;
        public string Description => description;
        public IReadOnlyList<string> SupportedOperations => operations ?? [];
        public bool IsReadOnly => isReadOnly;
        public bool IsConcurrencySafe => isConcurrencySafe;
        public bool IsDirectlyInvocable => isDirectlyInvocable;
        public BlastRadius RiskTier => risk;

        public Task<ToolResult> ExecuteAsync(
            string operation,
            IReadOnlyDictionary<string, object?> parameters,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Not used in catalog tests.");
    }
}
