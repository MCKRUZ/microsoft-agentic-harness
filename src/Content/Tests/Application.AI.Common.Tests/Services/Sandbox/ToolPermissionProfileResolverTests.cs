using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Tools;
using Application.AI.Common.Services.Sandbox;
using Application.AI.Common.Services.Tools;
using Domain.AI.Governance;
using Domain.AI.Sandbox;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.Sandbox;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.Services.Sandbox;

/// <summary>
/// Tests for <see cref="ToolPermissionProfileResolver"/>. The base declaration now comes from a
/// registered <see cref="ITool"/>'s own <see cref="ITool.RequiredCapabilities"/>/
/// <see cref="ITool.MinimumIsolation"/> via bounded-key-set keyed DI, mirroring
/// <c>ToolCapabilityResolverTests</c>'s pattern for the sibling composition-capability resolver —
/// not the dead <c>[ToolCapabilityAttribute]</c>/<c>RegisterToolType</c> mechanism this replaces
/// (#387).
/// </summary>
public sealed class ToolPermissionProfileResolverTests
{
    private static ToolPermissionProfileResolver BuildResolver(
        SandboxConfig? config = null,
        params (string Name, ITool Tool)[] tools)
    {
        return BuildResolver(config, auditService: null, tools);
    }

    private static ToolPermissionProfileResolver BuildResolver(
        SandboxConfig? config,
        IGovernanceAuditService? auditService,
        params (string Name, ITool Tool)[] tools)
    {
        return BuildResolver(config, auditService, governanceConfig: null, tools);
    }

    private static ToolPermissionProfileResolver BuildResolver(
        SandboxConfig? config,
        IGovernanceAuditService? auditService,
        IOptionsMonitor<GovernanceConfig>? governanceConfig,
        params (string Name, ITool Tool)[] tools)
    {
        var services = new ServiceCollection();
        foreach (var (name, tool) in tools)
            services.AddKeyedSingleton<ITool>(name, (_, _) => tool);

        var configMock = new Mock<IOptionsMonitor<SandboxConfig>>();
        configMock.Setup(m => m.CurrentValue).Returns(config ?? new SandboxConfig());

        var lookup = new FirstPartyToolLookup(
            services.BuildServiceProvider(), new HashSet<string>(tools.Select(t => t.Name)));
        return new ToolPermissionProfileResolver(lookup, configMock.Object, auditService, governanceConfig);
    }

    private static ITool FileTool() => Mock.Of<ITool>(t =>
        t.RequiredCapabilities == (ToolCapability.FileRead | ToolCapability.FileWrite));

    private static ITool FullTool() => Mock.Of<ITool>(t =>
        t.RequiredCapabilities == (ToolCapability.FileRead | ToolCapability.FileWrite | ToolCapability.NetworkAccess));

    private static ITool ContainerTool() => Mock.Of<ITool>(t =>
        t.RequiredCapabilities == ToolCapability.FileRead
        && t.MinimumIsolation == SandboxIsolationLevel.Container);

    [Fact]
    public void Resolve_UnregisteredName_NoOverride_ReturnsDefaultProfile()
    {
        var resolver = BuildResolver();

        var profile = resolver.Resolve("unknown_tool");

        profile.RequiredCapabilities.Should().Be(ToolCapability.None);
        profile.DeniedCapabilities.Should().Be(ToolCapability.None);
        profile.MinimumIsolation.Should().Be(SandboxIsolationLevel.None);
    }

    [Fact]
    public void Resolve_DeclarationOnly_ReturnsDeclaredValues()
    {
        var resolver = BuildResolver(tools: ("file_system", FileTool()));

        var profile = resolver.Resolve("file_system");

        profile.RequiredCapabilities.Should().Be(ToolCapability.FileRead | ToolCapability.FileWrite);
        profile.MinimumIsolation.Should().Be(SandboxIsolationLevel.None);
    }

    [Fact]
    public void Resolve_OverrideOnly_UnregisteredTool_DeniedCapabilitiesStillPopulatedButHasNoEffect()
    {
        // A deny against a tool with no declared requirement (None) has nothing to narrow — proves
        // AND-against-None stays None regardless of what an operator writes (#405; see
        // McpConnectionManager's remarks on why this matters for bundle-owned tool names).
        var config = new SandboxConfig
        {
            ToolOverrides = new()
            {
                ["custom_tool"] = new ToolOverrideConfig
                {
                    DeniedCapabilities = ["FileRead"],
                    MinimumIsolation = "Process"
                }
            }
        };
        var resolver = BuildResolver(config);

        var profile = resolver.Resolve("custom_tool");

        profile.RequiredCapabilities.Should().Be(ToolCapability.None);
        profile.DeniedCapabilities.Should().Be(ToolCapability.FileRead);
        profile.EffectiveCapabilities.Should().Be(ToolCapability.None);
        profile.MinimumIsolation.Should().Be(SandboxIsolationLevel.Process);
    }

    [Fact]
    public void Resolve_OverrideDeniedCapabilities_KeptSeparateFromRequired_NarrowsOnlyEffective()
    {
        // The core #405 fix: DeniedCapabilities must not be folded into RequiredCapabilities — the
        // tool's own declaration stays undiminished, and only EffectiveCapabilities (what sandbox
        // provisioning and the enforcer's grant check read) is narrowed.
        var config = new SandboxConfig
        {
            ToolOverrides = new()
            {
                ["full_tool"] = new ToolOverrideConfig { DeniedCapabilities = ["NetworkAccess"] }
            }
        };
        var resolver = BuildResolver(config, ("full_tool", FullTool()));

        var profile = resolver.Resolve("full_tool");

        profile.RequiredCapabilities.Should().Be(
            ToolCapability.FileRead | ToolCapability.FileWrite | ToolCapability.NetworkAccess,
            "the tool's own declaration must never be reduced by a deny override");
        profile.DeniedCapabilities.Should().Be(ToolCapability.NetworkAccess);
        profile.EffectiveCapabilities.Should().Be(ToolCapability.FileRead | ToolCapability.FileWrite);
    }

    [Fact]
    public void Resolve_OverrideMinimumIsolation_ElevatesButNeverDowngrades()
    {
        var config = new SandboxConfig
        {
            ToolOverrides = new()
            {
                ["container_tool"] = new ToolOverrideConfig { MinimumIsolation = "Process" }
            }
        };
        var resolver = BuildResolver(config, ("container_tool", ContainerTool()));

        var profile = resolver.Resolve("container_tool");

        profile.MinimumIsolation.Should().Be(SandboxIsolationLevel.Container);
    }

    [Fact]
    public void Resolve_NameNotInBoundedKeySet_IsTreatedAsUnregistered()
    {
        // A name that carries a real ITool registration in the container but was not included in the
        // bounded key set (e.g. an MCP or bundle-owned name) must never be probed — see the resolver's
        // remarks. Simulated here by registering the tool but passing an empty key set.
        var services = new ServiceCollection();
        services.AddKeyedSingleton<ITool>("mcp_tool", (_, _) => FullTool());
        var configMock = new Mock<IOptionsMonitor<SandboxConfig>>();
        configMock.Setup(m => m.CurrentValue).Returns(new SandboxConfig());
        var lookup = new FirstPartyToolLookup(services.BuildServiceProvider(), new HashSet<string>());
        var resolver = new ToolPermissionProfileResolver(lookup, configMock.Object);

        var profile = resolver.Resolve("mcp_tool");

        profile.RequiredCapabilities.Should().Be(ToolCapability.None);
    }

    [Fact]
    public void ParseCapabilities_ValidNames_ReturnsFlags()
    {
        var caps = ToolPermissionProfileResolver.ParseCapabilities(["FileRead", "NetworkAccess"]);

        caps.Should().Be(ToolCapability.FileRead | ToolCapability.NetworkAccess);
    }

    [Fact]
    public void ParseCapabilities_InvalidNames_IgnoresGracefully()
    {
        var caps = ToolPermissionProfileResolver.ParseCapabilities(["FileRead", "Bogus", "Subprocess"]);

        caps.Should().Be(ToolCapability.FileRead | ToolCapability.Subprocess);
    }

    [Fact]
    public void ParseCapabilities_Empty_ReturnsNone()
    {
        var caps = ToolPermissionProfileResolver.ParseCapabilities([]);

        caps.Should().Be(ToolCapability.None);
    }

    [Theory]
    [InlineData("255")]                     // every bit, including undefined ones
    [InlineData(" 255")]                    // and behind a stray space
    [InlineData("4")]                       // the numeric form of NetworkAccess
    [InlineData("Bogus")]
    public void ParseCapabilities_NumericOrUnknownEntry_IsIgnored(string entry)
    {
        // #300. ToolCapability is a [Flags] enum, so a permissive parse is worse here than
        // elsewhere: Enum.TryParse accepts "255" and sets every bit at once. On the granting side
        // (SandboxConfig.DefaultGrantedCapabilities, read by ToolInvocationGovernor) that hands a
        // tool every capability the sandbox model defines and makes the capability check unfailable.
        var caps = ToolPermissionProfileResolver.ParseCapabilities(["FileRead", entry]);

        caps.Should().Be(ToolCapability.FileRead);
    }

    [Fact]
    public void ParseCapabilities_CommaSeparatedNamesInOneEntry_AreAllHonoured()
    {
        // Deliberately NOT treated as a rejected composite, unlike every other enum in the #300
        // sweep. This method also feeds ToolOverrideConfig.DeniedCapabilities, where dropping an
        // entry fails OPEN — the capability stays granted, and ToolPermissionProfile.EffectiveCapabilities
        // (read by DockerContainerLaunchPreparer for container network access and read-only bind
        // mounts, and by CapabilityEnforcer for the grant check) resolves as if the deny were never
        // written. Refusing a comma entry would silently turn a working deny into a live grant on
        // upgrade. Each token is still validated by name individually, so the numeric form gains nothing.
        var caps = ToolPermissionProfileResolver.ParseCapabilities(["NetworkAccess, FileWrite"]);

        caps.Should().Be(ToolCapability.NetworkAccess | ToolCapability.FileWrite);
    }

    [Fact]
    public void Resolve_CommaSeparatedDeniedCapabilities_StillDeny()
    {
        // The regression this guards, stated where it actually bites: a deny that stops denying.
        var config = new SandboxConfig
        {
            ToolOverrides = new()
            {
                ["full_tool"] = new ToolOverrideConfig { DeniedCapabilities = ["NetworkAccess,FileWrite"] }
            }
        };
        var resolver = BuildResolver(config, ("full_tool", FullTool()));

        var profile = resolver.Resolve("full_tool");

        profile.DeniedCapabilities.Should().Be(ToolCapability.NetworkAccess | ToolCapability.FileWrite);
        profile.EffectiveCapabilities.Should().Be(ToolCapability.FileRead);
        profile.EffectiveCapabilities.Should().NotHaveFlag(ToolCapability.NetworkAccess);
        profile.EffectiveCapabilities.Should().NotHaveFlag(ToolCapability.FileWrite);
    }

    [Fact]
    public void Resolve_NumericDeniedCapability_IsIgnoredAndDoesNotDenyEverything()
    {
        // The other half of the contract: a numeric deny entry is refused rather than expanded to
        // every bit. "255" would otherwise strip all capabilities from the tool.
        var config = new SandboxConfig
        {
            ToolOverrides = new()
            {
                ["full_tool"] = new ToolOverrideConfig { DeniedCapabilities = ["255"] }
            }
        };
        var resolver = BuildResolver(config, ("full_tool", FullTool()));

        var profile = resolver.Resolve("full_tool");

        profile.DeniedCapabilities.Should().Be(ToolCapability.None);
        profile.EffectiveCapabilities.Should().Be(
            ToolCapability.FileRead | ToolCapability.FileWrite | ToolCapability.NetworkAccess);
    }

    [Fact]
    public void ParseCapabilities_NumericEntry_WouldOtherwiseGrantEveryCapability()
    {
        // Proof the guard is load-bearing rather than decorative: the framework call this replaces
        // accepts "255" and produces a value carrying every defined capability.
        Enum.TryParse<ToolCapability>("255", ignoreCase: true, out var viaFramework).Should().BeTrue();
        viaFramework.Should().HaveFlag(ToolCapability.Subprocess);
        viaFramework.Should().HaveFlag(ToolCapability.NetworkAccess);

        ToolPermissionProfileResolver.ParseCapabilities(["255"]).Should().Be(ToolCapability.None);
    }

    [Theory]
    [InlineData("99")]
    [InlineData("2")]                       // the numeric form of a real isolation level
    [InlineData("None,Container")]
    public void Resolve_NonNameMinimumIsolation_IsIgnoredAndTheDeclaredFloorStands(string configured)
    {
        // The override may only elevate isolation, so an unparseable value must land on None and
        // leave the tool's declared floor untouched — not on an isolation level that is not a
        // member, which Math.Max would then treat as higher than Container.
        var config = new SandboxConfig
        {
            ToolOverrides = new()
            {
                ["container_tool"] = new ToolOverrideConfig { MinimumIsolation = configured }
            }
        };
        var resolver = BuildResolver(config, ("container_tool", ContainerTool()));

        var profile = resolver.Resolve("container_tool");

        profile.MinimumIsolation.Should().Be(SandboxIsolationLevel.Container);
    }

    // --- ResolveForUngovernedDispatch (#405 — WorkspaceCommandRunner/IacSandboxRunner's shared
    // merge, previously only exercised indirectly through those two runners; direct unit coverage
    // added after a code-review finding on the duplicated refusal formula) ---

    [Fact]
    public void ResolveForUngovernedDispatch_NoOverride_Succeeds_FloorsIsolationAtProcess()
    {
        var resolver = BuildResolver();

        var result = resolver.ResolveForUngovernedDispatch(
            "unregistered_tool", ToolCapability.FileRead | ToolCapability.Subprocess, ["dotnet"]);

        result.IsSuccess.Should().BeTrue();
        result.Value!.RequiredCapabilities.Should().Be(ToolCapability.FileRead | ToolCapability.Subprocess);
        result.Value.DeniedCapabilities.Should().Be(ToolCapability.None);
        result.Value.AllowedPrograms.Should().ContainSingle().Which.Should().Be("dotnet");
        result.Value.MinimumIsolation.Should().Be(SandboxIsolationLevel.Process,
            "this dispatch path requires at least process isolation even with no operator override");
    }

    [Fact]
    public void ResolveForUngovernedDispatch_NonIntersectingDeny_Succeeds_CarriesTheDenyForward()
    {
        var config = new SandboxConfig
        {
            ToolOverrides = new()
            {
                ["iac_plan"] = new ToolOverrideConfig { DeniedCapabilities = ["DatabaseRead"] }
            }
        };
        var resolver = BuildResolver(config);

        var result = resolver.ResolveForUngovernedDispatch(
            "iac_plan",
            ToolCapability.FileRead | ToolCapability.FileWrite | ToolCapability.Subprocess | ToolCapability.NetworkAccess,
            ["terraform"]);

        result.IsSuccess.Should().BeTrue();
        result.Value!.DeniedCapabilities.Should().Be(ToolCapability.DatabaseRead);
    }

    [Fact]
    public void ResolveForUngovernedDispatch_IntersectingDeny_RefusesOutright()
    {
        var config = new SandboxConfig
        {
            ToolOverrides = new()
            {
                ["iac_plan"] = new ToolOverrideConfig { DeniedCapabilities = ["NetworkAccess"] }
            }
        };
        var resolver = BuildResolver(config);

        var result = resolver.ResolveForUngovernedDispatch(
            "iac_plan",
            ToolCapability.FileRead | ToolCapability.NetworkAccess,
            ["terraform"]);

        result.IsSuccess.Should().BeFalse(
            "a deny that intersects what the caller actually requires must refuse, not silently narrow");
        result.Errors.Should().ContainSingle(e => e.Contains("NetworkAccess"));
    }

    [Fact]
    public void ResolveForUngovernedDispatch_OverrideMinimumIsolation_ElevatesButNeverDowngrades()
    {
        var config = new SandboxConfig
        {
            ToolOverrides = new()
            {
                ["iac_plan"] = new ToolOverrideConfig { MinimumIsolation = "Container" }
            }
        };
        var resolver = BuildResolver(config);

        var result = resolver.ResolveForUngovernedDispatch(
            "iac_plan", ToolCapability.FileRead, ["terraform"]);

        result.IsSuccess.Should().BeTrue();
        result.Value!.MinimumIsolation.Should().Be(SandboxIsolationLevel.Container);
    }

    [Fact]
    public void ResolveForUngovernedDispatch_CallerDefaultIsolationLevel_IsReflectedInTheReturnedProfile()
    {
        // A code-review finding on this same follow-up: the returned profile used to hardcode
        // MinimumIsolation to Process regardless of the caller's own floor, so a caller constructed
        // with an elevated defaultIsolationLevel (WorkspaceCommandRunner/IacSandboxRunner both expose
        // this) got the right ISandboxExecutor selected — the caller computed the max itself — but
        // the profile embedded in the sandbox request still read Process. SandboxSessionAttestationSigner's
        // capabilitiesEnforcedBy field and a Docker-unavailable fallback gate both read this field, so
        // a caller with an elevated floor got the correct executor but a stale, mislabeled record. No
        // operator override configured here — the elevation must come from the caller's own parameter.
        var resolver = BuildResolver();

        var result = resolver.ResolveForUngovernedDispatch(
            "unregistered_tool", ToolCapability.FileRead, ["dotnet"],
            defaultIsolationLevel: SandboxIsolationLevel.Container);

        result.IsSuccess.Should().BeTrue();
        result.Value!.MinimumIsolation.Should().Be(SandboxIsolationLevel.Container,
            "the caller's own floor must reach the returned profile, not just the caller's local executor selection");
    }

    // --- Under-declaration cross-check (M6, a security-review finding on the #405 follow-up):
    // requiredCapabilities is the CALLER's own, separately-maintained declaration on this ungoverned
    // dispatch path — nothing previously stopped it from drifting under the tool's own registered
    // ITool.RequiredCapabilities. ---

    [Fact]
    public void ResolveForUngovernedDispatch_CallerUnderDeclaresRelativeToRegisteredTool_RefusesOutright()
    {
        // full_tool is registered declaring FileRead|FileWrite|NetworkAccess; the caller passes only
        // FileRead — missing FileWrite and NetworkAccess relative to the tool's own declaration.
        var resolver = BuildResolver(tools: ("full_tool", FullTool()));

        var result = resolver.ResolveForUngovernedDispatch(
            "full_tool", ToolCapability.FileRead, ["program"]);

        result.IsSuccess.Should().BeFalse(
            "a caller-supplied capability set narrower than the tool's own registered declaration " +
            "must refuse, not silently dispatch with less than the tool claims to need");
        result.Errors.Should().ContainSingle(e =>
            e.Contains("FileWrite") && e.Contains("NetworkAccess"));
    }

    [Fact]
    public void ResolveForUngovernedDispatch_CallerDeclarationMatchesRegisteredTool_Succeeds()
    {
        var resolver = BuildResolver(tools: ("full_tool", FullTool()));

        var result = resolver.ResolveForUngovernedDispatch(
            "full_tool",
            ToolCapability.FileRead | ToolCapability.FileWrite | ToolCapability.NetworkAccess,
            ["program"]);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void ResolveForUngovernedDispatch_CallerDeclarationExceedsRegisteredTool_Succeeds()
    {
        // Declaring MORE than the tool's own registration is not under-declaration — this check only
        // guards against declaring less.
        var resolver = BuildResolver(tools: ("file_tool", FileTool()));

        var result = resolver.ResolveForUngovernedDispatch(
            "file_tool",
            ToolCapability.FileRead | ToolCapability.FileWrite | ToolCapability.Subprocess,
            ["program"]);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void ResolveForUngovernedDispatch_ToolNameOutsideBoundedKeySet_SkipsUnderDeclarationCheck()
    {
        // A name outside the bounded first-party key set (e.g. MCP or bundle-owned) has nothing
        // registered to compare against — the under-declaration check must not fire for it.
        var resolver = BuildResolver();

        var result = resolver.ResolveForUngovernedDispatch(
            "mcp_tool", ToolCapability.None, ["program"]);

        result.IsSuccess.Should().BeTrue();
    }

    // --- Governance audit trail on refusal (#419 — every governed refusal already reaches
    // governance.jsonl via CapabilityEnforcer/ToolInvocationGovernor's use of the same
    // IGovernanceAuditService; a refusal on this ungoverned-dispatch path previously reached
    // neither that trail nor an app log (the app-log gap was closed separately in #421/#426)). ---

    [Fact]
    public void ResolveForUngovernedDispatch_IntersectingDeny_LogsDenialToAuditTrail()
    {
        var auditMock = new Mock<IGovernanceAuditService>();
        var config = new SandboxConfig
        {
            ToolOverrides = new()
            {
                ["iac_plan"] = new ToolOverrideConfig { DeniedCapabilities = ["NetworkAccess"] }
            }
        };
        var resolver = BuildResolver(config, auditMock.Object);

        resolver.ResolveForUngovernedDispatch(
            "iac_plan", ToolCapability.FileRead | ToolCapability.NetworkAccess, ["terraform"],
            agentId: "agent-42");

        auditMock.Verify(a => a.Log("agent-42", "iac_plan", ToolDecisionOutcome.Denied.ToString()), Times.Once);
    }

    [Fact]
    public void ResolveForUngovernedDispatch_UnderDeclaration_LogsDenialToAuditTrail()
    {
        var auditMock = new Mock<IGovernanceAuditService>();
        var resolver = BuildResolver(null, auditMock.Object, ("full_tool", FullTool()));

        resolver.ResolveForUngovernedDispatch(
            "full_tool", ToolCapability.FileRead, ["program"], agentId: "agent-7");

        auditMock.Verify(a => a.Log("agent-7", "full_tool", ToolDecisionOutcome.Denied.ToString()), Times.Once);
    }

    [Fact]
    public void ResolveForUngovernedDispatch_NoAgentIdSupplied_LogsUnknownRatherThanOmittingTheEntry()
    {
        var auditMock = new Mock<IGovernanceAuditService>();
        var config = new SandboxConfig
        {
            ToolOverrides = new()
            {
                ["iac_plan"] = new ToolOverrideConfig { DeniedCapabilities = ["NetworkAccess"] }
            }
        };
        var resolver = BuildResolver(config, auditMock.Object);

        resolver.ResolveForUngovernedDispatch(
            "iac_plan", ToolCapability.NetworkAccess, ["terraform"]);

        auditMock.Verify(a => a.Log("unknown", "iac_plan", ToolDecisionOutcome.Denied.ToString()), Times.Once);
    }

    [Fact]
    public void ResolveForUngovernedDispatch_Succeeds_NeverLogsToTheAuditTrail()
    {
        // A successful dispatch is not a governance decision — only a refusal is. Proves the audit
        // call is refusal-gated, not fired unconditionally on every call.
        var auditMock = new Mock<IGovernanceAuditService>();
        var resolver = BuildResolver(null, auditMock.Object);

        resolver.ResolveForUngovernedDispatch(
            "unregistered_tool", ToolCapability.FileRead, ["dotnet"]);

        auditMock.Verify(a => a.Log(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void ResolveForUngovernedDispatch_NoAuditServiceConfigured_RefusalStillWorksWithoutThrowing()
    {
        // The optional-dependency contract (#419): a composition root that never wires
        // IGovernanceAuditService must still get a working resolver, just with no durable audit
        // trail for this path — mirrors ProvenanceMemoryWriteGate's IGovernanceAuditService? convention.
        var config = new SandboxConfig
        {
            ToolOverrides = new()
            {
                ["iac_plan"] = new ToolOverrideConfig { DeniedCapabilities = ["NetworkAccess"] }
            }
        };
        var resolver = BuildResolver(config, auditService: null);

        var act = () => resolver.ResolveForUngovernedDispatch(
            "iac_plan", ToolCapability.NetworkAccess, ["terraform"]);

        act.Should().NotThrow();
        act().IsSuccess.Should().BeFalse();
    }

    [Fact]
    public void ResolveForUngovernedDispatch_EnableAuditFalse_SuppressesTheAuditLogCall()
    {
        // #419 code-review finding: every other IGovernanceAuditService call site in the codebase
        // (ToolInvocationGovernor, PromptInjectionBehavior) honors GovernanceConfig.EnableAudit — an
        // operator who disables it must not keep seeing writes to governance.jsonl from this path alone.
        var auditMock = new Mock<IGovernanceAuditService>();
        var governanceConfigMock = new Mock<IOptionsMonitor<GovernanceConfig>>();
        governanceConfigMock.Setup(m => m.CurrentValue).Returns(new GovernanceConfig { EnableAudit = false });
        var config = new SandboxConfig
        {
            ToolOverrides = new()
            {
                ["iac_plan"] = new ToolOverrideConfig { DeniedCapabilities = ["NetworkAccess"] }
            }
        };
        var resolver = BuildResolver(config, auditMock.Object, governanceConfigMock.Object);

        var result = resolver.ResolveForUngovernedDispatch(
            "iac_plan", ToolCapability.NetworkAccess, ["terraform"]);

        result.IsSuccess.Should().BeFalse("the refusal itself must still happen — only the audit write is gated");
        auditMock.Verify(a => a.Log(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void ResolveForUngovernedDispatch_EnableAuditTrue_StillLogsToTheAuditTrail()
    {
        var auditMock = new Mock<IGovernanceAuditService>();
        var governanceConfigMock = new Mock<IOptionsMonitor<GovernanceConfig>>();
        governanceConfigMock.Setup(m => m.CurrentValue).Returns(new GovernanceConfig { EnableAudit = true });
        var config = new SandboxConfig
        {
            ToolOverrides = new()
            {
                ["iac_plan"] = new ToolOverrideConfig { DeniedCapabilities = ["NetworkAccess"] }
            }
        };
        var resolver = BuildResolver(config, auditMock.Object, governanceConfigMock.Object);

        resolver.ResolveForUngovernedDispatch(
            "iac_plan", ToolCapability.NetworkAccess, ["terraform"], agentId: "agent-1");

        auditMock.Verify(a => a.Log("agent-1", "iac_plan", ToolDecisionOutcome.Denied.ToString()), Times.Once);
    }

    [Fact]
    public void ResolveExecutorForUngovernedDispatch_RefusalOnResolvedTier_ForwardsAgentIdFromScopedExecutionContext()
    {
        // The one production wiring point for agentId (#419): IAgentExecutionContext is scoped, so it
        // is read from the caller's own per-execution scope — never captured on this singleton's
        // constructor — and forwarded down to ResolveForUngovernedDispatch's audit call.
        var auditMock = new Mock<IGovernanceAuditService>();
        var config = new SandboxConfig
        {
            ToolOverrides = new()
            {
                ["iac_plan"] = new ToolOverrideConfig { DeniedCapabilities = ["NetworkAccess"] }
            }
        };
        var resolver = BuildResolver(config, auditMock.Object);
        var executionContext = Mock.Of<IAgentExecutionContext>(c => c.AgentId == "scoped-agent-99");
        var scopedServices = new ServiceCollection()
            .AddSingleton(executionContext)
            .BuildServiceProvider();

        resolver.ResolveExecutorForUngovernedDispatch(
            "iac_plan", ToolCapability.NetworkAccess, ["terraform"], scopedServices);

        auditMock.Verify(a => a.Log("scoped-agent-99", "iac_plan", ToolDecisionOutcome.Denied.ToString()), Times.Once);
    }

    [Fact]
    public void ResolveExecutorForUngovernedDispatch_RefusalWithNoExecutionContextInScope_LogsUnknown()
    {
        var auditMock = new Mock<IGovernanceAuditService>();
        var config = new SandboxConfig
        {
            ToolOverrides = new()
            {
                ["iac_plan"] = new ToolOverrideConfig { DeniedCapabilities = ["NetworkAccess"] }
            }
        };
        var resolver = BuildResolver(config, auditMock.Object);
        var scopedServices = new ServiceCollection().BuildServiceProvider();

        resolver.ResolveExecutorForUngovernedDispatch(
            "iac_plan", ToolCapability.NetworkAccess, ["terraform"], scopedServices);

        auditMock.Verify(a => a.Log("unknown", "iac_plan", ToolDecisionOutcome.Denied.ToString()), Times.Once);
    }

    // --- ResolveExecutorForUngovernedDispatch (a /simplify finding: WorkspaceCommandRunner and
    // IacSandboxRunner each independently resolved the profile then separately selected the executor
    // from it, with only a comment protecting the ordering — folded into one method here so the
    // invariant is structural, not reproduced per caller.) ---

    private static IServiceProvider ScopedServices(SandboxIsolationLevel level, Application.AI.Common.Interfaces.Sandbox.ISandboxExecutor executor) =>
        new ServiceCollection().AddKeyedSingleton(level, executor).BuildServiceProvider();

    [Fact]
    public void ResolveExecutorForUngovernedDispatch_Success_ResolvesTheExecutorForTheProfilesTier()
    {
        var executor = Mock.Of<Application.AI.Common.Interfaces.Sandbox.ISandboxExecutor>();
        var config = new SandboxConfig
        {
            ToolOverrides = new()
            {
                ["iac_plan"] = new ToolOverrideConfig { MinimumIsolation = "Container" }
            }
        };
        var resolver = BuildResolver(config);

        var result = resolver.ResolveExecutorForUngovernedDispatch(
            "iac_plan", ToolCapability.FileRead, ["terraform"],
            ScopedServices(SandboxIsolationLevel.Container, executor));

        result.IsSuccess.Should().BeTrue();
        result.Value!.Profile.MinimumIsolation.Should().Be(SandboxIsolationLevel.Container);
        result.Value!.Executor.Should().BeSameAs(executor,
            "the executor must be resolved for the profile's resolved tier, not a fixed default");
    }

    [Fact]
    public void ResolveExecutorForUngovernedDispatch_ProfileForbidden_NeverResolvesAnExecutor()
    {
        var config = new SandboxConfig
        {
            ToolOverrides = new()
            {
                ["iac_plan"] = new ToolOverrideConfig { DeniedCapabilities = ["NetworkAccess"] }
            }
        };
        var resolver = BuildResolver(config);
        // An empty scope: if this were ever reached, GetRequiredKeyedService would throw — proving
        // the refusal short-circuits before any executor lookup is attempted.
        var emptyScope = new ServiceCollection().BuildServiceProvider();

        var result = resolver.ResolveExecutorForUngovernedDispatch(
            "iac_plan", ToolCapability.FileRead | ToolCapability.NetworkAccess, ["terraform"], emptyScope);

        result.IsSuccess.Should().BeFalse();
    }
}
