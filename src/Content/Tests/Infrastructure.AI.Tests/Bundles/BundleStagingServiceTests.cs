using System.IO.Compression;
using System.Text;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Plugins;
using Domain.AI.Governance;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.BundleExecution;
using Domain.Common.Config.AI.MCP;
using Domain.Common.Config.AI.Sandbox;
using FluentAssertions;
using Infrastructure.AI.Agents;
using Infrastructure.AI.Bundles;
using Infrastructure.AI.Plugins;
using Infrastructure.AI.Skills;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Bundles;

/// <summary>
/// Tests for <see cref="BundleStagingService"/> — the boundary at which an untrusted, externally-authored
/// agent bundle is validated and extracted before any of its content is trusted. Each hostile-archive
/// guard (oversize, entry count, decompression bomb, zip-slip, discovery-root overlap) gets a test, plus
/// the happy path that a well-formed bundle yields a staged agent with its owned skills on disk.
/// </summary>
public sealed class BundleStagingServiceTests : IDisposable
{
    private readonly string _stagingRoot =
        Path.Combine(Path.GetTempPath(), $"bundle-staging-tests-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_stagingRoot))
            Directory.Delete(_stagingRoot, recursive: true);
    }

    // --- Happy path ---------------------------------------------------------------------------------

    [Fact]
    public async Task StageAsync_WellFormedBundle_StagesAgentAndOwnedSkillOnDisk()
    {
        using var zip = ZipOf(
            ("AGENT.md", "---\nid: my-bundle\nname: My Bundle\nskills: [greet]\n---\nBundle instructions."),
            ("skills/greet/SKILL.md", "---\nid: greet\nname: greet\n---\nGreet the user."),
            ("plugin.json", "{ \"name\": \"my-plugin\", \"version\": \"1.0.0\" }"));

        var result = await CreateService().StageAsync(zip);

        result.IsSuccess.Should().BeTrue(string.Join("; ", result.Errors));
        var bundle = result.Value!;
        bundle.Agent.Id.Should().Be("my-bundle");
        bundle.Agent.Instructions.Should().Contain("Bundle instructions.");
        bundle.OwnedSkills.Select(s => s.Id).Should().ContainSingle(id => id == "greet");
        bundle.PluginManifests.Select(m => m.Name).Should().ContainSingle(n => n == "my-plugin");
        bundle.McpServerNames.Should().BeEmpty("this bundle's plugin.json declares no mcpServers");

        // The staged files really live under the staging root, so disk-based skill disclosure can read them.
        Directory.Exists(bundle.StagedRootDirectory).Should().BeTrue();
        bundle.StagedRootDirectory.Should().StartWith(_stagingRoot);
        File.Exists(Path.Combine(bundle.StagedRootDirectory, "AGENT.md")).Should().BeTrue();
        bundle.OwnedSkills[0].BaseDirectory.Should().StartWith(bundle.StagedRootDirectory);
    }

    [Fact]
    public async Task StageAsync_NestedSkillDirWithoutSkillMd_IsSkippedNotFatal()
    {
        // A skills/ subdirectory with no SKILL.md (or an otherwise unreadable one) must be skipped without
        // failing the whole bundle — the good nested skill still stages.
        using var zip = ZipOf(
            ("AGENT.md", "---\nid: b\nname: B\n---\nx"),
            ("skills/good/SKILL.md", "---\nid: good\nname: good\n---\nA valid skill."),
            ("skills/empty/notes.txt", "no SKILL.md here"));

        var result = await CreateService().StageAsync(zip);

        result.IsSuccess.Should().BeTrue(string.Join("; ", result.Errors));
        result.Value!.OwnedSkills.Select(s => s.Id).Should().BeEquivalentTo(["good"]);
    }

    // --- Manifest injection scanning (issue #331) ----------------------------------------------------

    /// <summary>
    /// Unlike a flagged skill (skip-and-continue, above), a flagged AGENT.md fails the whole bundle —
    /// deliberately: the agent manifest is the bundle's identity, so there is no sensible "continue
    /// without it" outcome. <see cref="AgentMetadataParser.ParseFromFile"/> throws
    /// <c>ManifestRefusedException</c>, which is not caught inside <c>ParseStagedBundle</c> and
    /// propagates to <c>StageAsync</c>'s outer catch, which cleans up and fails the bundle.
    /// </summary>
    [Fact]
    public async Task StageAsync_PoisonedAgentMd_FailsTheWholeBundleAndCleansUp()
    {
        using var zip = ZipOf(
            ("AGENT.md", "---\nid: poisoned\nname: Poisoned\n---\nx"),
            ("skills/greet/SKILL.md", "---\nid: greet\nname: greet\n---\nA valid skill."));

        var appConfig = new AppConfig
        {
            AI = new AIConfig
            {
                BundleExecution = new BundleExecutionConfig { TempRoot = _stagingRoot },
                Governance = new GovernanceConfig { EnableMcpSecurity = true },
            }
        };
        var result = await CreateService(appConfig, agentScanner: WithheldScanner()).StageAsync(zip);

        result.IsSuccess.Should().BeFalse("a poisoned agent manifest is the bundle's identity — there is no sensible partial outcome");
        result.Errors.Should().ContainMatch("*Bundle staging failed*");
        NoStagedDirectoriesRemain();
    }

    /// <summary>
    /// A flagged skill inside an otherwise-clean bundle is skipped, not fatal — the same
    /// skip-and-continue behaviour <see cref="StageAsync_NestedSkillDirWithoutSkillMd_IsSkippedNotFatal"/>
    /// already covers for an unreadable skill, now proven for a scan refusal specifically:
    /// <c>NestedSkillScanner</c> catches <c>SkillMetadataParser.ParseFromFile</c>'s exception
    /// (any type other than <c>SkillPathRefusedException</c>) before it can reach the bundle-level catch.
    /// </summary>
    [Fact]
    public async Task StageAsync_PoisonedSkillInBundle_IsSkippedRestOfBundleStages()
    {
        using var zip = ZipOf(
            ("AGENT.md", "---\nid: b\nname: B\n---\nx"),
            ("skills/good/SKILL.md", "---\nid: good\nname: good\n---\nA valid skill."),
            ("skills/bad/SKILL.md", "---\nid: bad\nname: bad\n---\nA flagged skill."));

        var appConfig = new AppConfig
        {
            AI = new AIConfig
            {
                BundleExecution = new BundleExecutionConfig { TempRoot = _stagingRoot },
                Governance = new GovernanceConfig { EnableMcpSecurity = true },
            }
        };
        var result = await CreateService(appConfig, skillScanner: WithheldScannerForSource("bad")).StageAsync(zip);

        result.IsSuccess.Should().BeTrue(string.Join("; ", result.Errors));
        result.Value!.OwnedSkills.Select(s => s.Id).Should().BeEquivalentTo(["good"]);
    }

    private static IMcpSecurityScanner WithheldScanner()
    {
        var mock = new Mock<IMcpSecurityScanner>();
        mock.Setup(s => s.ScanContent(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()))
            .Returns((string name, string _, bool _) =>
                new McpToolScanResult(name, false, [new McpToolThreat(McpThreatType.InstructionPoisoning, ThreatLevel.High, "test finding", 0.9)]));
        return mock.Object;
    }

    private static IMcpSecurityScanner WithheldScannerForSource(string sourceName)
    {
        var mock = new Mock<IMcpSecurityScanner>();
        mock.Setup(s => s.ScanContent(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()))
            .Returns((string name, string _, bool _) => name == sourceName
                ? new McpToolScanResult(name, false, [new McpToolThreat(McpThreatType.InstructionPoisoning, ThreatLevel.High, "test finding", 0.9)])
                : McpToolScanResult.Safe(name));
        return mock.Object;
    }

    // --- Bundle-owned MCP servers (issue #368) -------------------------------------------------------

    [Fact]
    public async Task StageAsync_BundleWithDefaultedStdioMcpServer_IsRejectedNotRegistered()
    {
        // No "type" property, so McpServerDefinitionBuilder.ParseType DEFAULTS this to stdio — the
        // gate treats a defaulted stdio resolution differently from an EXPLICIT "type": "stdio"
        // declaration (see StageAsync_ExplicitStdioServer_RegistersWhenEnabled below): only an explicit
        // declaration can ever reach the sandboxed-registration path, so this must still be rejected via
        // LogStdioRejected even with every stdio capability flag turned on.
        using var zip = ZipOf(
            ("AGENT.md", "---\nid: mcp-bundle\nname: MCP Bundle\n---\nx"),
            ("plugin.json", "{ \"name\": \"root\", \"version\": \"1.0.0\", \"mcpServers\": \"./mcp.json\" }"),
            ("mcp.json", "{ \"mcpServers\": { \"echo\": { \"command\": \"npx\", \"args\": [\"echo-mcp\"] } } }"));

        var bundleOwnedMcpServers = new BundleOwnedMcpServerRegistry();
        // AllowBundleDeclaredMcpServers must be ON here — otherwise the flag-off guard short-circuits
        // RegisterBundleMcpServers before mcp.json is even parsed, and this test would pass for that
        // reason instead of exercising the stdio-transport rejection it names and documents. StdioMcpServers
        // is also fully enabled with a configured image, to prove the rejection is about the MISSING
        // explicit declaration, not about either capability being off.
        var appConfig = StdioEnabledAppConfig();
        var result = await CreateService(appConfig, bundleOwnedMcpServers).StageAsync(zip);

        result.IsSuccess.Should().BeTrue(string.Join("; ", result.Errors));
        var bundle = result.Value!;
        var namespacedName = $"{bundle.BundleId}:echo";

        bundle.McpServerNames.Should().BeEmpty("a defaulted (non-explicit) stdio server must be rejected, not registered");
        bundleOwnedMcpServers.TryGetValue(namespacedName, out _).Should().BeFalse();
    }

    [Fact]
    public async Task StageAsync_ExplicitStdioServer_RegistersWhenEnabled()
    {
        using var zip = ZipOf(
            ("AGENT.md", "---\nid: stdio-bundle\nname: Stdio Bundle\n---\nx"),
            ("plugin.json", "{ \"name\": \"root\", \"version\": \"1.0.0\", \"mcpServers\": \"./mcp.json\" }"),
            ("mcp.json", "{ \"mcpServers\": { \"echo\": { \"type\": \"stdio\", \"command\": \"npx\", \"args\": [\"echo-mcp\"] } } }"));

        var bundleOwnedMcpServers = new BundleOwnedMcpServerRegistry();
        var appConfig = StdioEnabledAppConfig();
        var result = await CreateService(appConfig, bundleOwnedMcpServers).StageAsync(zip);

        result.IsSuccess.Should().BeTrue(string.Join("; ", result.Errors));
        var bundle = result.Value!;
        var namespacedName = $"{bundle.BundleId}:echo";

        bundle.McpServerNames.Should().ContainSingle().Which.Should().Be(namespacedName);
        bundleOwnedMcpServers.TryGetValue(namespacedName, out var definition).Should().BeTrue();
        definition!.Type.Should().Be(McpServerType.Stdio);
        definition.Command.Should().Be("npx");
        definition.SandboxSeedDirectory.Should().Be(bundle.StagedRootDirectory,
            "the sandbox session needs the bundle's own staged files to seed the container workspace");
    }

    [Fact]
    public async Task StageAsync_ExplicitStdioServer_RejectedWhenStdioFlagOff()
    {
        using var zip = ZipOf(
            ("AGENT.md", "---\nid: stdio-off-bundle\nname: Stdio Off Bundle\n---\nx"),
            ("plugin.json", "{ \"name\": \"root\", \"version\": \"1.0.0\", \"mcpServers\": \"./mcp.json\" }"),
            ("mcp.json", "{ \"mcpServers\": { \"echo\": { \"type\": \"stdio\", \"command\": \"npx\" } } }"));

        var bundleOwnedMcpServers = new BundleOwnedMcpServerRegistry();
        // AllowBundleDeclaredMcpServers must be ON here so RegisterBundleMcpServers' own top-level gate
        // (either capability admits the manifest) doesn't short-circuit before mcp.json is ever parsed —
        // otherwise this test would pass for that reason instead of exercising StdioMcpServers.Enabled's
        // OWN check inside TryRegisterStdioServer, which is what it names and documents.
        var appConfig = StdioEnabledAppConfig(stdioServersEnabled: false);
        appConfig.AI.BundleExecution.AllowBundleDeclaredMcpServers = true;
        var result = await CreateService(appConfig, bundleOwnedMcpServers).StageAsync(zip);

        result.IsSuccess.Should().BeTrue(string.Join("; ", result.Errors));
        result.Value!.McpServerNames.Should().BeEmpty("the stdio capability is off by default even for an explicit declaration");
    }

    [Fact]
    public async Task StageAsync_ExplicitStdioServer_RejectedWhenNoContainerImageConfigured()
    {
        using var zip = ZipOf(
            ("AGENT.md", "---\nid: no-image-bundle\nname: No Image Bundle\n---\nx"),
            ("plugin.json", "{ \"name\": \"root\", \"version\": \"1.0.0\", \"mcpServers\": \"./mcp.json\" }"),
            ("mcp.json", "{ \"mcpServers\": { \"echo\": { \"type\": \"stdio\", \"command\": \"npx\" } } }"));

        var bundleOwnedMcpServers = new BundleOwnedMcpServerRegistry();
        var appConfig = StdioEnabledAppConfig(containerImage: string.Empty);
        var result = await CreateService(appConfig, bundleOwnedMcpServers).StageAsync(zip);

        result.IsSuccess.Should().BeTrue(string.Join("; ", result.Errors));
        result.Value!.McpServerNames.Should().BeEmpty(
            "an unconfigured container image means the capability is inert even when the flag is on");
    }

    [Fact]
    public async Task StageAsync_ExplicitStdioServer_RejectedWhenSandboxDisabled()
    {
        using var zip = ZipOf(
            ("AGENT.md", "---\nid: sandbox-off-bundle\nname: Sandbox Off Bundle\n---\nx"),
            ("plugin.json", "{ \"name\": \"root\", \"version\": \"1.0.0\", \"mcpServers\": \"./mcp.json\" }"),
            ("mcp.json", "{ \"mcpServers\": { \"echo\": { \"type\": \"stdio\", \"command\": \"npx\" } } }"));

        var bundleOwnedMcpServers = new BundleOwnedMcpServerRegistry();
        var appConfig = StdioEnabledAppConfig(sandboxEnabled: false);
        var result = await CreateService(appConfig, bundleOwnedMcpServers).StageAsync(zip);

        result.IsSuccess.Should().BeTrue(string.Join("; ", result.Errors));
        result.Value!.McpServerNames.Should().BeEmpty(
            "registering a server that could never start would be pointless");
    }

    [Fact]
    public async Task StageAsync_StdioServerWithEmptyCommand_Rejected()
    {
        using var zip = ZipOf(
            ("AGENT.md", "---\nid: empty-command-bundle\nname: Empty Command Bundle\n---\nx"),
            ("plugin.json", "{ \"name\": \"root\", \"version\": \"1.0.0\", \"mcpServers\": \"./mcp.json\" }"),
            ("mcp.json", "{ \"mcpServers\": { \"echo\": { \"type\": \"stdio\" } } }"));

        var bundleOwnedMcpServers = new BundleOwnedMcpServerRegistry();
        var appConfig = StdioEnabledAppConfig();
        var result = await CreateService(appConfig, bundleOwnedMcpServers).StageAsync(zip);

        result.IsSuccess.Should().BeTrue(string.Join("; ", result.Errors));
        result.Value!.McpServerNames.Should().BeEmpty(
            "McpServerDefinitionBuilder does not itself require a command for stdio; this gate must");
    }

    [Fact]
    public async Task StageAsync_UnrecognizedTypeValue_StillRejected()
    {
        // A typo'd remote-transport name ("htp") must not silently land on a sandboxed process launch —
        // only an explicit, exact "stdio" counts as an intentional local-command declaration.
        using var zip = ZipOf(
            ("AGENT.md", "---\nid: typo-bundle\nname: Typo Bundle\n---\nx"),
            ("plugin.json", "{ \"name\": \"root\", \"version\": \"1.0.0\", \"mcpServers\": \"./mcp.json\" }"),
            ("mcp.json", "{ \"mcpServers\": { \"echo\": { \"type\": \"htp\", \"command\": \"npx\" } } }"));

        var bundleOwnedMcpServers = new BundleOwnedMcpServerRegistry();
        var appConfig = StdioEnabledAppConfig();
        var result = await CreateService(appConfig, bundleOwnedMcpServers).StageAsync(zip);

        result.IsSuccess.Should().BeTrue(string.Join("; ", result.Errors));
        result.Value!.McpServerNames.Should().BeEmpty("an unrecognized type value must never be treated as an explicit stdio request");
    }

    [Fact]
    public async Task StageAsync_StdioServersBeyondCap_Rejected()
    {
        using var zip = ZipOf(
            ("AGENT.md", "---\nid: capped-bundle\nname: Capped Bundle\n---\nx"),
            ("plugin.json", "{ \"name\": \"root\", \"version\": \"1.0.0\", \"mcpServers\": \"./mcp.json\" }"),
            ("mcp.json", "{ \"mcpServers\": { " +
                "\"first\": { \"type\": \"stdio\", \"command\": \"npx\" }, " +
                "\"second\": { \"type\": \"stdio\", \"command\": \"npx\" } } }"));

        var bundleOwnedMcpServers = new BundleOwnedMcpServerRegistry();
        var appConfig = StdioEnabledAppConfig(maxServersPerBundle: 1);
        var result = await CreateService(appConfig, bundleOwnedMcpServers).StageAsync(zip);

        result.IsSuccess.Should().BeTrue(string.Join("; ", result.Errors));
        result.Value!.McpServerNames.Should().ContainSingle(
            "the per-bundle cap of 1 must admit the first declared server and reject the second");
    }

    [Fact]
    public async Task StageAsync_RejectedStdioAttemptFollowedByValidOne_ValidOneStillRegisters()
    {
        // A stdio server rejected for a reason OTHER than the cap (here: empty command) must not
        // itself consume a cap slot — only a server that actually registers may count against
        // MaxServersPerBundle. With the cap set to 1, if a rejected attempt wrongly counted, this
        // bundle's genuinely valid second server would be refused too, even though zero servers
        // are actually registered yet.
        using var zip = ZipOf(
            ("AGENT.md", "---\nid: rejected-then-valid-bundle\nname: Rejected Then Valid Bundle\n---\nx"),
            ("plugin.json", "{ \"name\": \"root\", \"version\": \"1.0.0\", \"mcpServers\": \"./mcp.json\" }"),
            ("mcp.json", "{ \"mcpServers\": { " +
                "\"first\": { \"type\": \"stdio\" }, " +
                "\"second\": { \"type\": \"stdio\", \"command\": \"npx\" } } }"));

        var bundleOwnedMcpServers = new BundleOwnedMcpServerRegistry();
        var appConfig = StdioEnabledAppConfig(maxServersPerBundle: 1);
        var result = await CreateService(appConfig, bundleOwnedMcpServers).StageAsync(zip);

        result.IsSuccess.Should().BeTrue(string.Join("; ", result.Errors));
        var namespacedName = $"{result.Value!.BundleId}:second";
        result.Value!.McpServerNames.Should().ContainSingle().Which.Should().Be(namespacedName,
            "the first server's empty-command rejection must not consume the per-bundle cap slot " +
            "the second, valid server needs");
    }

    [Fact]
    public async Task StageAsync_BundleWithHttpMcpServer_RegistersHttpDefinitionWithUrl()
    {
        using var zip = ZipOf(
            ("AGENT.md", "---\nid: http-bundle\nname: HTTP Bundle\n---\nx"),
            ("plugin.json", "{ \"name\": \"root\", \"version\": \"1.0.0\", \"mcpServers\": \"./mcp.json\" }"),
            ("mcp.json", "{ \"mcpServers\": { \"remote\": { \"type\": \"http\", \"url\": \"https://tools.example.com/mcp\" } } }"));

        var bundleOwnedMcpServers = new BundleOwnedMcpServerRegistry();
        var appConfig = AllowlistedAppConfig("tools.example.com");
        var result = await CreateService(appConfig, bundleOwnedMcpServers).StageAsync(zip);

        result.IsSuccess.Should().BeTrue(string.Join("; ", result.Errors));
        var namespacedName = $"{result.Value!.BundleId}:remote";

        bundleOwnedMcpServers.TryGetValue(namespacedName, out var definition).Should().BeTrue();
        definition!.Type.Should().Be(McpServerType.Http);
        definition.Url.Should().Be("https://tools.example.com/mcp");
    }

    [Fact]
    public async Task StageAsync_BundleHttpMcpServerWithUnparsableUrl_IsRejectedNotSilentlyAllowlistedThrough()
    {
        // Regression test: an http/sse server whose declared "url" is not a valid absolute URI must be
        // REJECTED by the registration-time allowlist gate, not fall through it unchecked. Uri.TryCreate
        // failing used to skip the whole allowlist-check block (including the rejection), silently
        // registering an unvalidated destination.
        using var zip = ZipOf(
            ("AGENT.md", "---\nid: bad-url-bundle\nname: Bad URL Bundle\n---\nx"),
            ("plugin.json", "{ \"name\": \"root\", \"version\": \"1.0.0\", \"mcpServers\": \"./mcp.json\" }"),
            ("mcp.json", "{ \"mcpServers\": { \"remote\": { \"type\": \"http\", \"url\": \"not-a-valid-absolute-uri\" } } }"));

        var bundleOwnedMcpServers = new BundleOwnedMcpServerRegistry();
        // Allowlist is irrelevant to this test's assertion — even a wide-open allowlist must not rescue
        // a URL that never resolved to a checkable host in the first place.
        var appConfig = AllowlistedAppConfig("not-a-valid-absolute-uri");
        var result = await CreateService(appConfig, bundleOwnedMcpServers).StageAsync(zip);

        result.IsSuccess.Should().BeTrue(string.Join("; ", result.Errors));
        result.Value!.McpServerNames.Should().BeEmpty(
            "an unparsable URL must be rejected at registration time, not silently registered");
        var namespacedName = $"{result.Value.BundleId}:remote";
        bundleOwnedMcpServers.TryGetValue(namespacedName, out _).Should().BeFalse();
    }

    [Fact]
    public async Task StageAsync_RemoteServer_RejectedWhenOnlyStdioFlagOn()
    {
        // Regression: RegisterBundleMcpServers' own top-level gate now opens mcp.json when EITHER
        // AllowBundleDeclaredMcpServers OR StdioMcpServers.Enabled is on (#371, so a bundle declaring
        // only a stdio server doesn't need the remote flag). TryBuildAndRegisterOneServer's remote
        // branch must therefore carry its OWN AllowBundleDeclaredMcpServers check — without it, opting
        // into stdio alone would silently also open the door to remote servers.
        using var zip = ZipOf(
            ("AGENT.md", "---\nid: remote-via-stdio-flag-bundle\nname: X\n---\nx"),
            ("plugin.json", "{ \"name\": \"root\", \"version\": \"1.0.0\", \"mcpServers\": \"./mcp.json\" }"),
            ("mcp.json", "{ \"mcpServers\": { \"remote\": { \"type\": \"http\", \"url\": \"https://tools.example.com/mcp\" } } }"));

        var bundleOwnedMcpServers = new BundleOwnedMcpServerRegistry();
        var appConfig = StdioEnabledAppConfig();
        appConfig.AI.Egress = new EgressConfig
        {
            DefaultAllowlist = [new EgressAllowlistConfigEntry { Host = "tools.example.com", Schemes = ["https"], Ports = [443] }],
        };
        var result = await CreateService(appConfig, bundleOwnedMcpServers).StageAsync(zip);

        result.IsSuccess.Should().BeTrue(string.Join("; ", result.Errors));
        result.Value!.McpServerNames.Should().BeEmpty(
            "AllowBundleDeclaredMcpServers stayed off in this config — a remote server must not register " +
            "just because the unrelated stdio capability happened to be on");
    }

    [Fact]
    public async Task StageAsync_MalformedMcpJson_DegradesGracefullyWithoutFailingStaging()
    {
        using var zip = ZipOf(
            ("AGENT.md", "---\nid: bad-mcp\nname: Bad MCP\n---\nx"),
            ("plugin.json", "{ \"name\": \"root\", \"version\": \"1.0.0\", \"mcpServers\": \"./mcp.json\" }"),
            ("mcp.json", "{ this is not valid json"));

        var bundleOwnedMcpServers = new BundleOwnedMcpServerRegistry();
        var result = await CreateService(bundleOwnedMcpServers: bundleOwnedMcpServers).StageAsync(zip);

        result.IsSuccess.Should().BeTrue("a malformed mcp.json must degrade, not fail the whole bundle");
        result.Value!.McpServerNames.Should().BeEmpty(
            "TryBuildAndRegisterOneServer only ever adds a name here on a successful registry TryAdd, " +
            "so an empty list already proves nothing reached bundleOwnedMcpServers");
    }

    [Fact]
    public async Task StageAsync_McpServersPropertyIsNotAnObject_DegradesGracefullyWithoutFailingStaging()
    {
        // Regression test: mcp.json's "mcpServers" property parses as VALID json but the wrong shape (a
        // string here, not an object) — EnumerateObject() on a non-object JsonElement throws, which used
        // to propagate out of ReadMcpServersBlock uncaught and fail the whole bundle upload, contradicting
        // this method's own documented "never fails staging" contract.
        using var zip = ZipOf(
            ("AGENT.md", "---\nid: wrong-shape-mcp\nname: Wrong Shape MCP\n---\nx"),
            ("plugin.json", "{ \"name\": \"root\", \"version\": \"1.0.0\", \"mcpServers\": \"./mcp.json\" }"),
            ("mcp.json", "{ \"mcpServers\": \"not-an-object\" }"));

        var bundleOwnedMcpServers = new BundleOwnedMcpServerRegistry();
        var result = await CreateService(bundleOwnedMcpServers: bundleOwnedMcpServers).StageAsync(zip);

        result.IsSuccess.Should().BeTrue("a non-object mcpServers value must degrade, not fail the whole bundle");
        result.Value!.McpServerNames.Should().BeEmpty(
            "TryBuildAndRegisterOneServer only ever adds a name here on a successful registry TryAdd, " +
            "so an empty list already proves nothing reached bundleOwnedMcpServers");
    }

    [Fact]
    public async Task StageAsync_TwoNestedPluginsDeclareSameServerName_KeepsFirstAndDoesNotThrow()
    {
        using var zip = ZipOf(
            ("AGENT.md", "---\nid: dup-mcp\nname: Dup MCP\n---\nx"),
            ("plugins/one/plugin.json", "{ \"name\": \"one\", \"version\": \"1.0.0\", \"mcpServers\": \"./mcp.json\" }"),
            ("plugins/one/mcp.json", "{ \"mcpServers\": { \"shared\": { \"type\": \"http\", \"url\": \"https://one.example.com/mcp\" } } }"),
            ("plugins/two/plugin.json", "{ \"name\": \"two\", \"version\": \"1.0.0\", \"mcpServers\": \"./mcp.json\" }"),
            ("plugins/two/mcp.json", "{ \"mcpServers\": { \"shared\": { \"type\": \"http\", \"url\": \"https://two.example.com/mcp\" } } }"));

        var bundleOwnedMcpServers = new BundleOwnedMcpServerRegistry();
        var appConfig = AllowlistedAppConfig("one.example.com", "two.example.com");
        var result = await CreateService(appConfig, bundleOwnedMcpServers).StageAsync(zip);

        result.IsSuccess.Should().BeTrue(string.Join("; ", result.Errors));
        result.Value!.McpServerNames.Should().ContainSingle(
            "TryBuildAndRegisterOneServer only ever adds a name here on a successful registry TryAdd, " +
            "so a single returned name already proves exactly one entry reached bundleOwnedMcpServers");
    }

    [Fact]
    public async Task StageAsync_LaterManifestThrows_RollsBackEarlierManifestsRegisteredServers()
    {
        // Regression test for #372. The ROOT manifest registers a valid MCP server; a SINGLE nested
        // plugin's manifest read throws (simulating any exception RegisterBundleMcpServers or the plugin
        // reader can produce). Deliberately structured to be order-independent, not reliant on
        // Directory.EnumerateDirectories' unspecified ordering (a /code-review finding on an earlier
        // version of this test, which used two nested plugins and could pass vacuously if the throwing
        // one happened to enumerate first — the assertion below would hold trivially because the "good"
        // manifest was never reached, proving nothing about rollback): the root manifest is ALWAYS read
        // before ParsePluginManifests even starts enumerating plugins/, and there is exactly one nested
        // directory here, so there is no cross-item ordering left to depend on. Before this fix, the root
        // manifest's registry entry survived — staging failed and deleted the extracted files, but
        // nothing ever called TryRemove for a server registered before the failure, orphaning it for the
        // life of the host process.
        using var zip = ZipOf(
            ("AGENT.md", "---\nid: rollback-bundle\nname: Rollback Bundle\n---\nx"),
            ("plugin.json", "{ \"name\": \"root\", \"version\": \"1.0.0\", \"mcpServers\": \"./mcp.json\" }"),
            ("mcp.json", "{ \"mcpServers\": { \"good-server\": { \"type\": \"http\", \"url\": \"https://one.example.com/mcp\" } } }"),
            ("plugins/bad/plugin.json", "{ \"name\": \"bad\", \"version\": \"1.0.0\" }"));

        var bundleOwnedMcpServers = new BundleOwnedMcpServerRegistry();
        var appConfig = AllowlistedAppConfig("one.example.com");
        var realReader = new PluginManifestReader(NullLogger<PluginManifestReader>.Instance);
        string? bundleDir = null;

        var throwingReader = new Mock<IPluginManifestReader>();
        throwingReader
            .Setup(r => r.Read(It.IsAny<string>()))
            .Returns((string dir) =>
            {
                var normalized = dir.Replace('\\', '/');
                if (normalized.EndsWith("/plugins/bad", StringComparison.Ordinal))
                    throw new InvalidOperationException("simulated manifest-parsing failure");
                if (!normalized.Contains("/plugins/", StringComparison.Ordinal))
                    bundleDir = dir; // the root-manifest call always passes the bundle's own staged directory
                return realReader.Read(dir);
            });

        var result = await CreateService(appConfig, bundleOwnedMcpServers, pluginReader: throwingReader.Object)
            .StageAsync(zip);

        result.IsSuccess.Should().BeFalse("a later manifest's parsing failure must fail the whole bundle");
        bundleDir.Should().NotBeNull("the root manifest must have been read before the failing nested one");
        var bundleId = Path.GetFileName(bundleDir);
        bundleOwnedMcpServers.TryGetValue($"{bundleId}:good-server", out _).Should().BeFalse(
            "the root manifest's server registration must be rolled back when a later manifest throws");
        NoStagedDirectoriesRemain();
    }

    // --- Hostile-archive guards ---------------------------------------------------------------------

    [Fact]
    public async Task StageAsync_ZipSlipEntry_IsRejectedAndLeavesNothingBehind()
    {
        using var zip = ZipOf(
            ("AGENT.md", "---\nid: a\nname: A\n---\nx"),
            ("../escape.txt", "pwned"));

        var result = await CreateService().StageAsync(zip);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().ContainMatch("*escapes the staging directory*");
        NoStagedDirectoriesRemain();
        File.Exists(Path.Combine(Path.GetDirectoryName(_stagingRoot)!, "escape.txt")).Should().BeFalse();
    }

    [Fact]
    public async Task StageAsync_TooManyEntries_IsRejected()
    {
        using var zip = ZipOf(
            ("AGENT.md", "---\nid: a\nname: A\n---\nx"),
            ("a.txt", "1"),
            ("b.txt", "2"));

        var result = await CreateService(new BundleExecutionConfig { MaxEntryCount = 2 }).StageAsync(zip);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().ContainMatch("*more than the maximum 2 entries*");
    }

    [Fact]
    public async Task StageAsync_ExceedsMaxUncompressedSize_IsRejected()
    {
        using var zip = ZipOf(("AGENT.md", new string('x', 4096)));

        var result = await CreateService(new BundleExecutionConfig { MaxTotalUncompressedBytes = 64 }).StageAsync(zip);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().ContainMatch("*expands to more than the maximum 64 bytes*");
        NoStagedDirectoriesRemain();
    }

    [Fact]
    public async Task StageAsync_ExceedsMaxArchiveSize_IsRejected()
    {
        using var zip = ZipOf(("AGENT.md", new string('x', 4096)));

        var result = await CreateService(new BundleExecutionConfig { MaxArchiveBytes = 16 }).StageAsync(zip);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().ContainMatch("*exceeds the maximum accepted size*");
    }

    [Fact]
    public async Task StageAsync_HighCompressionRatio_IsRejected()
    {
        // ~2 MiB of a single repeated byte compresses to a few KB — a ratio far above the default 100,
        // and above the 1 MiB floor at which the ratio guard engages.
        using var zip = ZipOf(("AGENT.md", new string('a', 2 * 1024 * 1024)));

        var result = await CreateService().StageAsync(zip);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().ContainMatch("*compression ratio exceeds*");
    }

    [Fact]
    public async Task StageAsync_NotAZip_IsRejected()
    {
        using var garbage = new MemoryStream("this is not a zip archive"u8.ToArray());

        var result = await CreateService().StageAsync(garbage);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().ContainMatch("*not a valid zip archive*");
    }

    [Fact]
    public async Task StageAsync_EmptyArchive_IsRejected()
    {
        using var empty = new MemoryStream();

        var result = await CreateService().StageAsync(empty);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().ContainMatch("*empty*");
    }

    [Fact]
    public async Task StageAsync_MissingAgentMd_IsRejectedAndCleansUp()
    {
        using var zip = ZipOf(("skills/greet/SKILL.md", "---\nid: greet\nname: greet\n---\nx"));

        var result = await CreateService().StageAsync(zip);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().ContainMatch("*no AGENT.md at its root*");
        NoStagedDirectoriesRemain();
    }

    [Fact]
    public async Task StageAsync_StagingRootOverlapsAgentDiscoveryRoot_IsRejected()
    {
        using var zip = ZipOf(("AGENT.md", "---\nid: a\nname: A\n---\nx"));

        // Point the agent discovery root at the very staging root: the global registry would then be
        // able to discover the bundle's agent/skills, defeating isolation — staging must refuse.
        var appConfig = new AppConfig
        {
            AI = new AIConfig
            {
                BundleExecution = new BundleExecutionConfig { TempRoot = _stagingRoot },
                Agents = new AgentsConfig { BasePath = _stagingRoot },
            }
        };

        var result = await CreateService(appConfig).StageAsync(zip);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().ContainMatch("*overlaps a configured skill or agent discovery root*");
    }

    // --- Helpers ------------------------------------------------------------------------------------

    private BundleStagingService CreateService(
        BundleExecutionConfig? overrides = null, BundleOwnedMcpServerRegistry? bundleOwnedMcpServers = null)
    {
        var cfg = overrides ?? new BundleExecutionConfig();
        cfg.TempRoot = _stagingRoot;
        return CreateService(new AppConfig { AI = new AIConfig { BundleExecution = cfg } }, bundleOwnedMcpServers);
    }

    /// <summary>
    /// An <see cref="AppConfig"/> that opts into bundle-declared remote MCP servers
    /// (<see cref="BundleExecutionConfig.AllowBundleDeclaredMcpServers"/>) and allowlists the given hosts on
    /// <c>AI.Egress.DefaultAllowlist</c> — the registration-time destination check added for the PR #370
    /// security fix rejects any bundle-declared server whose host isn't present here.
    /// </summary>
    private AppConfig AllowlistedAppConfig(params string[] hosts) => new()
    {
        AI = new AIConfig
        {
            BundleExecution = new BundleExecutionConfig
            {
                TempRoot = _stagingRoot,
                AllowBundleDeclaredMcpServers = true,
            },
            Egress = new EgressConfig
            {
                DefaultAllowlist = hosts
                    .Select(host => new EgressAllowlistConfigEntry
                    {
                        Host = host,
                        Schemes = ["https"],
                        Ports = [443],
                    })
                    .ToList(),
            },
        },
    };

    /// <summary>
    /// An <see cref="AppConfig"/> fully opted into bundle-owned <strong>stdio</strong> MCP servers:
    /// <see cref="BundleStdioMcpServersConfig.Enabled"/> on, a container image configured, and
    /// (via the class default) the sandbox subsystem enabled — the three preconditions
    /// <c>TryRegisterStdioServer</c> checks beyond the explicit-declaration and non-empty-command gates
    /// that live in the manifest content itself. Each parameter defaults to the "capability fully on"
    /// value so a test only needs to override the ONE precondition it means to violate.
    /// </summary>
    private AppConfig StdioEnabledAppConfig(
        bool stdioServersEnabled = true,
        string containerImage = "mcr.microsoft.com/dotnet/runtime:10.0",
        int maxServersPerBundle = 2,
        bool sandboxEnabled = true) => new()
    {
        AI = new AIConfig
        {
            BundleExecution = new BundleExecutionConfig
            {
                TempRoot = _stagingRoot,
                StdioMcpServers = new BundleStdioMcpServersConfig
                {
                    Enabled = stdioServersEnabled,
                    ContainerImage = containerImage,
                    MaxServersPerBundle = maxServersPerBundle,
                },
            },
            SandboxCapabilities = new SandboxConfig { Enabled = sandboxEnabled },
        },
    };

    private static BundleStagingService CreateService(
        AppConfig appConfig,
        BundleOwnedMcpServerRegistry? bundleOwnedMcpServers = null,
        IMcpSecurityScanner? agentScanner = null,
        IMcpSecurityScanner? skillScanner = null,
        IPluginManifestReader? pluginReader = null) =>
        new(
            new OptionsMonitorStub(appConfig),
            new AgentMetadataParser(
                NullLogger<AgentMetadataParser>.Instance, agentScanner ?? TestMcpSecurityScanner.AlwaysSafe(),
                Mock.Of<IOptionsMonitor<AIConfig>>(m => m.CurrentValue == appConfig.AI)),
            new SkillMetadataParser(
                NullLogger<SkillMetadataParser>.Instance, new UnsandboxedSkillFileReader(),
                skillScanner ?? TestMcpSecurityScanner.AlwaysSafe(),
                Mock.Of<IOptionsMonitor<AIConfig>>(m => m.CurrentValue == appConfig.AI)),
            new UnsandboxedSkillFileReader(),
            pluginReader ?? new PluginManifestReader(NullLogger<PluginManifestReader>.Instance),
            bundleOwnedMcpServers ?? new BundleOwnedMcpServerRegistry(),
            NullLogger<BundleStagingService>.Instance);

    private void NoStagedDirectoriesRemain()
    {
        if (Directory.Exists(_stagingRoot))
            Directory.GetDirectories(_stagingRoot).Should().BeEmpty("a rejected bundle must leave no partial extraction");
    }

    private static MemoryStream ZipOf(params (string Path, string Content)[] entries)
    {
        var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in entries)
            {
                var entry = archive.CreateEntry(path);
                using var stream = entry.Open();
                var bytes = Encoding.UTF8.GetBytes(content);
                stream.Write(bytes, 0, bytes.Length);
            }
        }

        ms.Position = 0;
        return ms;
    }

    private sealed class OptionsMonitorStub(AppConfig value) : IOptionsMonitor<AppConfig>
    {
        public AppConfig CurrentValue { get; } = value;
        public AppConfig Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<AppConfig, string?> listener) => null;
    }
}
