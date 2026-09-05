using Application.AI.Common.Interfaces.Sandbox;
using Application.AI.Common.Interfaces.Tools;
using Application.AI.Common.Services.Sandbox;
using Application.AI.Common.Services.Tools;
using Domain.AI.Sandbox;
using Domain.Common;
using Domain.Common.Config.AI.Sandbox;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.Behaviors;

/// <summary>
/// Tests for <see cref="CapabilityEnforcer"/>/<see cref="ToolPermissionProfileResolver"/>. A tool's
/// base declaration comes from a registered <see cref="ITool"/>'s own
/// <see cref="ITool.RequiredCapabilities"/>/<see cref="ITool.MinimumIsolation"/> via keyed DI, not
/// the dead <c>[ToolCapabilityAttribute]</c>/<c>RegisterToolType</c> mechanism this replaces (#387).
/// </summary>
public sealed class CapabilityEnforcementTests
{
    // CapabilityEnforcer now refuses any requested path that is not OS-rooted (#418 CI hardening —
    // a relative path is unsafe to compare here at all, since it would resolve against a base this
    // class has no visibility into). A literal "C:/..." fixture is rooted on Windows but NOT on
    // Linux (CI runs on ubuntu-latest), so every path fixture below is built from this OS-correct
    // root rather than hardcoded, or every "should be allowed"/"sibling doesn't match" assertion
    // would silently become "always refused" off this machine.
    private static readonly string Root = OperatingSystem.IsWindows() ? "C:/" : "/";

    private static ITool FileTool() => Mock.Of<ITool>(t =>
        t.RequiredCapabilities == (ToolCapability.FileRead | ToolCapability.FileWrite));

    private static ITool NetworkFileTool() => Mock.Of<ITool>(t =>
        t.RequiredCapabilities == (ToolCapability.FileRead | ToolCapability.NetworkAccess));

    private static ITool FullTool() => Mock.Of<ITool>(t =>
        t.RequiredCapabilities == (ToolCapability.FileRead | ToolCapability.FileWrite | ToolCapability.NetworkAccess));

    private static ITool ReadOnlyTool() => Mock.Of<ITool>(t =>
        t.RequiredCapabilities == ToolCapability.FileRead);

    // The old [ToolCapability] attribute defaulted MinimumIsolation to Process when a tool declared
    // one without setting it explicitly; ITool.MinimumIsolation defaults to None instead (matching
    // every production tool, none of which ever carried the dead attribute). This fake preserves the
    // "a declared floor is honoured" scenario by declaring Process explicitly.
    private static ITool ProcessIsolationTool() => Mock.Of<ITool>(t =>
        t.RequiredCapabilities == (ToolCapability.FileRead | ToolCapability.FileWrite)
        && t.MinimumIsolation == SandboxIsolationLevel.Process);

    private static ITool MinimalIsolationTool() => Mock.Of<ITool>(t =>
        t.RequiredCapabilities == ToolCapability.FileRead
        && t.MinimumIsolation == SandboxIsolationLevel.None);

    private static (ToolPermissionProfileResolver Resolver, CapabilityEnforcer Enforcer) Build(
        SandboxConfig? config = null,
        params (string Name, ITool Tool)[] tools)
    {
        var services = new ServiceCollection();
        foreach (var (name, tool) in tools)
            services.AddKeyedSingleton<ITool>(name, (_, _) => tool);

        var configMock = new Mock<IOptionsMonitor<SandboxConfig>>();
        configMock.Setup(m => m.CurrentValue).Returns(config ?? new SandboxConfig());

        var lookup = new FirstPartyToolLookup(
            services.BuildServiceProvider(), new HashSet<string>(tools.Select(t => t.Name)));
        var resolver = new ToolPermissionProfileResolver(lookup, configMock.Object);
        var enforcer = new CapabilityEnforcer(resolver, Mock.Of<ILogger<CapabilityEnforcer>>());
        return (resolver, enforcer);
    }

    // --- Capability Checks ---

    [Fact]
    public async Task AllCapabilitiesGranted_PassesThrough()
    {
        var (_, enforcer) = Build(tools: ("file_system", FileTool()));

        var result = await enforcer.EnforceAsync(
            "file_system",
            ToolCapability.FileRead | ToolCapability.FileWrite | ToolCapability.NetworkAccess);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task MissingCapability_ReturnsFail()
    {
        var (_, enforcer) = Build(tools: ("network_file", NetworkFileTool()));

        var result = await enforcer.EnforceAsync(
            "network_file",
            ToolCapability.FileRead);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("NetworkAccess"));
    }

    [Fact]
    public async Task DeniedCapability_ThatToolRequires_FailsEnforcement()
    {
        // The core #405 behavior change: a tool whose requirement intersects its own per-tool deny
        // is refused outright by EnforceAsync, even when the caller granted every capability the
        // tool's undiminished declaration lists.
        var config = new SandboxConfig
        {
            ToolOverrides = new()
            {
                ["full_tool"] = new ToolOverrideConfig { DeniedCapabilities = ["NetworkAccess"] }
            }
        };
        var (_, enforcer) = Build(config, ("full_tool", FullTool()));

        var result = await enforcer.EnforceAsync(
            "full_tool",
            ToolCapability.FileRead | ToolCapability.FileWrite | ToolCapability.NetworkAccess);

        result.IsSuccess.Should().BeFalse(
            "a per-tool deny must refuse the call, not silently shrink the requirement");
    }

    // --- appsettings Override Behavior ---

    [Fact]
    public async Task AppsettingsOverride_KeepsDeclarationUndiminished_NarrowsOnlyEffective()
    {
        var config = new SandboxConfig
        {
            ToolOverrides = new()
            {
                ["full_tool"] = new ToolOverrideConfig { DeniedCapabilities = ["NetworkAccess"] }
            }
        };
        var (_, enforcer) = Build(config, ("full_tool", FullTool()));

        var profile = await enforcer.ResolveProfileAsync("full_tool", CancellationToken.None);

        profile.RequiredCapabilities.Should().Be(
            ToolCapability.FileRead | ToolCapability.FileWrite | ToolCapability.NetworkAccess,
            "the tool's own declaration must never be reduced by a deny override");
        profile.EffectiveCapabilities.Should().Be(ToolCapability.FileRead | ToolCapability.FileWrite);
    }

    [Fact]
    public async Task AppsettingsOverride_CannotExpandBeyondDeclaration()
    {
        var config = new SandboxConfig
        {
            ToolOverrides = new()
            {
                ["read_tool"] = new ToolOverrideConfig()
            }
        };
        var (_, enforcer) = Build(config, ("read_tool", ReadOnlyTool()));

        var profile = await enforcer.ResolveProfileAsync("read_tool", CancellationToken.None);

        profile.RequiredCapabilities.Should().Be(ToolCapability.FileRead);
    }

    // --- Adversarial / Edge Cases ---

    [Fact]
    public async Task UnregisteredTool_NoCapabilitiesRequired_PassesThrough()
    {
        var (_, enforcer) = Build();

        var result = await enforcer.EnforceAsync(
            "unknown_tool",
            ToolCapability.FileRead);

        result.IsSuccess.Should().BeTrue();
    }

    // --- Profile Resolution ---

    [Fact]
    public async Task Resolution_DeclarationFallbackWhenNoOverride()
    {
        var (_, enforcer) = Build(tools: ("file_system", ProcessIsolationTool()));

        var profile = await enforcer.ResolveProfileAsync("file_system", CancellationToken.None);

        profile.RequiredCapabilities.Should().Be(
            ToolCapability.FileRead | ToolCapability.FileWrite);
        profile.MinimumIsolation.Should().Be(SandboxIsolationLevel.Process);
    }

    [Fact]
    public async Task Resolution_OverrideTakesPrecedence()
    {
        var config = new SandboxConfig
        {
            ToolOverrides = new()
            {
                ["minimal_tool"] = new ToolOverrideConfig
                {
                    MinimumIsolation = "Process",
                    DeniedCapabilities = ["FileRead"]
                }
            }
        };
        var (_, enforcer) = Build(config, ("minimal_tool", MinimalIsolationTool()));

        var profile = await enforcer.ResolveProfileAsync("minimal_tool", CancellationToken.None);

        profile.MinimumIsolation.Should().Be(SandboxIsolationLevel.Process);
        profile.DeniedCapabilities.Should().Be(ToolCapability.FileRead);
    }

    // --- Path/host scoping (#418) ---

    [Fact]
    public async Task DeniedPath_ExactMatch_Refuses()
    {
        var config = new SandboxConfig
        {
            ToolOverrides = new() { ["file_system"] = new ToolOverrideConfig { DeniedPaths = [$"{Root}sandbox/secrets"] } }
        };
        var (_, enforcer) = Build(config, ("file_system", FileTool()));

        var result = await enforcer.EnforceAsync(
            "file_system", ToolCapability.FileRead | ToolCapability.FileWrite,
            requestedPaths: [$"{Root}sandbox/secrets/creds.txt"]);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("path denied"));
    }

    [Fact]
    public async Task DeniedPath_SiblingDirectory_DoesNotMatch()
    {
        // The sibling-directory bypass IsPathWithin exists to prevent: a boundary of
        // ".../sandbox/work" must not match ".../sandbox/work-evil" via a raw string prefix check.
        var config = new SandboxConfig
        {
            ToolOverrides = new() { ["file_system"] = new ToolOverrideConfig { DeniedPaths = [$"{Root}sandbox/work"] } }
        };
        var (_, enforcer) = Build(config, ("file_system", FileTool()));

        var result = await enforcer.EnforceAsync(
            "file_system", ToolCapability.FileRead | ToolCapability.FileWrite,
            requestedPaths: [$"{Root}sandbox/work-evil/file.txt"]);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task DeniedPath_RelativeTraversal_Refuses()
    {
        // CI-caught regression: the naive normalizer this replaced silently dropped a leading ".."
        // instead of resolving or rejecting it, so "../secrets/creds.txt" matched no configured
        // boundary at all. CapabilityEnforcer has no base directory to resolve a relative traversal
        // against (that's IFileSystemService's own concern), so it must refuse any traversal pattern
        // outright rather than guess where it points.
        var config = new SandboxConfig
        {
            ToolOverrides = new() { ["file_system"] = new ToolOverrideConfig { DeniedPaths = [$"{Root}sandbox/secrets"] } }
        };
        var (_, enforcer) = Build(config, ("file_system", FileTool()));

        var result = await enforcer.EnforceAsync(
            "file_system", ToolCapability.FileRead | ToolCapability.FileWrite,
            requestedPaths: ["../secrets/creds.txt"]);

        result.IsSuccess.Should().BeFalse("a path this class cannot safely resolve must not be silently allowed");
    }

    [Fact]
    public async Task DeniedPath_RelativeNonTraversal_Refuses()
    {
        // CI-caught regression: an ordinary relative path with no ".." at all still cannot be safely
        // compared here — CapabilityEnforcer would resolve it against the process's own working
        // directory, while IFileSystemService resolves the identical string against its own,
        // separately-configured sandbox base. The two can name different files entirely, so any
        // non-rooted path is refused outright, not just one that looks like a traversal attempt.
        var config = new SandboxConfig
        {
            ToolOverrides = new() { ["file_system"] = new ToolOverrideConfig { DeniedPaths = [$"{Root}sandbox/secrets"] } }
        };
        var (_, enforcer) = Build(config, ("file_system", FileTool()));

        var result = await enforcer.EnforceAsync(
            "file_system", ToolCapability.FileRead | ToolCapability.FileWrite,
            requestedPaths: ["secrets/creds.txt"]);

        result.IsSuccess.Should().BeFalse("a relative path resolves against an unknown base and must not be silently allowed");
    }

    [Fact]
    public async Task DeniedPath_WindowsDriveRelative_Refuses()
    {
        // Windows-only: "C:secrets\creds.txt" (no separator after the drive letter) is
        // Path.IsPathRooted == true but Path.IsPathFullyQualified == false — it still resolves
        // against that drive's current directory, the same CWD-dependent ambiguity a plain relative
        // path has. A rootedness-only check would have let it slip through the guard meant to catch
        // exactly this.
        if (!OperatingSystem.IsWindows())
            return;

        var config = new SandboxConfig
        {
            ToolOverrides = new() { ["file_system"] = new ToolOverrideConfig { DeniedPaths = ["C:/sandbox/secrets"] } }
        };
        var (_, enforcer) = Build(config, ("file_system", FileTool()));

        var result = await enforcer.EnforceAsync(
            "file_system", ToolCapability.FileRead | ToolCapability.FileWrite,
            requestedPaths: ["C:secrets\\creds.txt"]);

        result.IsSuccess.Should().BeFalse("a drive-relative path is not fully qualified and must not be silently allowed");
    }

    [Fact]
    public async Task DeniedPath_ConfiguredEntryUnparsable_StillRefuses()
    {
        // A configured DeniedPaths entry the runtime cannot normalize (here, an embedded NUL — one
        // of Path.GetInvalidPathChars() on every platform) must not be silently excluded from
        // matching — that would turn an operator's deny rule into a no-op instead of the refusal it
        // was written to enforce. The entry itself, not the requested path, is what's malformed here.
        var config = new SandboxConfig
        {
            ToolOverrides = new() { ["file_system"] = new ToolOverrideConfig { DeniedPaths = ["C:/sandbox/bad\0name"] } }
        };
        var (_, enforcer) = Build(config, ("file_system", FileTool()));

        var result = await enforcer.EnforceAsync(
            "file_system", ToolCapability.FileRead | ToolCapability.FileWrite,
            requestedPaths: [$"{Root}sandbox/unrelated.txt"]);

        result.IsSuccess.Should().BeFalse("an unparsable deny entry must fail closed, not silently exclude itself");
    }

    [Fact]
    public async Task DeniedPath_WinsOverAllowedPath()
    {
        var config = new SandboxConfig
        {
            ToolOverrides = new()
            {
                ["file_system"] = new ToolOverrideConfig
                {
                    AllowedPaths = [$"{Root}sandbox"],
                    DeniedPaths = [$"{Root}sandbox/secrets"]
                }
            }
        };
        var (_, enforcer) = Build(config, ("file_system", FileTool()));

        var result = await enforcer.EnforceAsync(
            "file_system", ToolCapability.FileRead | ToolCapability.FileWrite,
            requestedPaths: [$"{Root}sandbox/secrets/creds.txt"]);

        result.IsSuccess.Should().BeFalse("deny overrides allow even when the path is also within an allowed boundary");
    }

    [Fact]
    public async Task AllowedPathConfigured_RequestOutsideIt_Refuses()
    {
        var config = new SandboxConfig
        {
            ToolOverrides = new() { ["file_system"] = new ToolOverrideConfig { AllowedPaths = [$"{Root}sandbox/work"] } }
        };
        var (_, enforcer) = Build(config, ("file_system", FileTool()));

        var result = await enforcer.EnforceAsync(
            "file_system", ToolCapability.FileRead | ToolCapability.FileWrite,
            requestedPaths: [$"{Root}other/place.txt"]);

        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task PathScopingConfigured_RequestedPathsIsNull_RefusesFailClosed()
    {
        // The actual fix #418 delivers: a configured deny that cannot verify a call's resource
        // usage must refuse, not silently let it through — the exact fail-open shape #405 shipped
        // (requestedPaths: null and [] were treated identically).
        var config = new SandboxConfig
        {
            ToolOverrides = new() { ["file_system"] = new ToolOverrideConfig { DeniedPaths = [$"{Root}sandbox/secrets"] } }
        };
        var (_, enforcer) = Build(config, ("file_system", FileTool()));

        var result = await enforcer.EnforceAsync(
            "file_system", ToolCapability.FileRead | ToolCapability.FileWrite, requestedPaths: null);

        result.IsSuccess.Should().BeFalse("a configured deny with unknown resource usage must refuse, not pass through");
    }

    [Fact]
    public async Task NoPathScopingConfigured_RequestedPathsIsNull_PassesThrough()
    {
        var (_, enforcer) = Build(tools: ("file_system", FileTool()));

        var result = await enforcer.EnforceAsync(
            "file_system", ToolCapability.FileRead | ToolCapability.FileWrite, requestedPaths: null);

        result.IsSuccess.Should().BeTrue("no scoping is configured, so an unknown request is not a violation");
    }

    [Fact]
    public async Task PathScopingConfigured_RequestedPathsIsEmpty_PassesThrough()
    {
        // Empty means "determined, and there is none" — the tool's own affirmative declaration,
        // distinct from null ("could not be determined").
        var config = new SandboxConfig
        {
            ToolOverrides = new() { ["file_system"] = new ToolOverrideConfig { DeniedPaths = [$"{Root}sandbox/secrets"] } }
        };
        var (_, enforcer) = Build(config, ("file_system", FileTool()));

        var result = await enforcer.EnforceAsync(
            "file_system", ToolCapability.FileRead | ToolCapability.FileWrite, requestedPaths: []);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task DeniedHost_WildcardMatch_Refuses()
    {
        var config = new SandboxConfig
        {
            ToolOverrides = new() { ["http_tool"] = new ToolOverrideConfig { DeniedHosts = ["*.evil.com"] } }
        };
        var (_, enforcer) = Build(config, ("http_tool", NetworkFileTool()));

        var result = await enforcer.EnforceAsync(
            "http_tool", ToolCapability.FileRead | ToolCapability.NetworkAccess,
            requestedHosts: ["api.evil.com:443"]);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("host denied"));
    }

    [Fact]
    public async Task AllowedHostConfigured_ExactMatchWithPortStripped_PassesThrough()
    {
        var config = new SandboxConfig
        {
            ToolOverrides = new() { ["http_tool"] = new ToolOverrideConfig { AllowedHosts = ["api.example.com"] } }
        };
        var (_, enforcer) = Build(config, ("http_tool", NetworkFileTool()));

        var result = await enforcer.EnforceAsync(
            "http_tool", ToolCapability.FileRead | ToolCapability.NetworkAccess,
            requestedHosts: ["api.example.com:8443"]);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task DeniedHost_TrailingDotFqdn_StillMatches()
    {
        // A root-terminated FQDN ("host.") is the same name as "host" — a raw EndsWith comparison
        // let the trailing dot defeat a *.suffix deny entry entirely.
        var config = new SandboxConfig
        {
            ToolOverrides = new() { ["http_tool"] = new ToolOverrideConfig { DeniedHosts = ["*.evil.com"] } }
        };
        var (_, enforcer) = Build(config, ("http_tool", NetworkFileTool()));

        var result = await enforcer.EnforceAsync(
            "http_tool", ToolCapability.FileRead | ToolCapability.NetworkAccess,
            requestedHosts: ["api.evil.com."]);

        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task DeniedHost_BareIPv6Literal_StillMatches()
    {
        // StripPort's old "last colon, digits after" rule truncated a bare IPv6 literal ("::1"
        // becomes ":"), so a deny entry for it could never match.
        var config = new SandboxConfig
        {
            ToolOverrides = new() { ["http_tool"] = new ToolOverrideConfig { DeniedHosts = ["::1"] } }
        };
        var (_, enforcer) = Build(config, ("http_tool", NetworkFileTool()));

        var result = await enforcer.EnforceAsync(
            "http_tool", ToolCapability.FileRead | ToolCapability.NetworkAccess,
            requestedHosts: ["::1"]);

        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task DeniedHost_UrlSchemeAndPath_StillMatches()
    {
        // A resource-parameter of Host kind could carry a full URL, not a bare host — the old
        // single-colon check treated the whole string as unstrippable and compared it verbatim.
        var config = new SandboxConfig
        {
            ToolOverrides = new() { ["http_tool"] = new ToolOverrideConfig { DeniedHosts = ["evil.com"] } }
        };
        var (_, enforcer) = Build(config, ("http_tool", NetworkFileTool()));

        var result = await enforcer.EnforceAsync(
            "http_tool", ToolCapability.FileRead | ToolCapability.NetworkAccess,
            requestedHosts: ["https://evil.com/exfil"]);

        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task AllowedHost_UrlWithEmbeddedSchemeLaterInString_DoesNotMatchTheEmbeddedHost()
    {
        // Regression: an unanchored scan for "://" matches the FIRST occurrence anywhere in the
        // string, so a value like "good.com/redirect?to=http://evil.com" would incorrectly reduce to
        // "evil.com" — misdirecting the comparison at a host embedded later in the string rather than
        // the value's own leading host. Uri.TryCreate(..., UriKind.Absolute) refuses to parse this
        // (no leading scheme), so it must NOT be treated as if its host were "evil.com" — an
        // AllowedHosts entry for "good.com" must not silently reject it as if it named "evil.com".
        var config = new SandboxConfig
        {
            ToolOverrides = new() { ["http_tool"] = new ToolOverrideConfig { AllowedHosts = ["evil.com"] } }
        };
        var (_, enforcer) = Build(config, ("http_tool", NetworkFileTool()));

        var result = await enforcer.EnforceAsync(
            "http_tool", ToolCapability.FileRead | ToolCapability.NetworkAccess,
            requestedHosts: ["good.com/redirect?to=http://evil.com"]);

        result.IsSuccess.Should().BeFalse(
            "the value has no leading scheme so it must not be reduced to the unrelated embedded host \"evil.com\"");
    }

    [Fact]
    public async Task DeniedHost_ConfiguredEntryCarriesPort_StillMatchesBareRequestedHost()
    {
        // The port was only ever stripped from the requested host, never from the configured
        // pattern — an operator entry of "evil.com:443" could never match anything.
        var config = new SandboxConfig
        {
            ToolOverrides = new() { ["http_tool"] = new ToolOverrideConfig { DeniedHosts = ["evil.com:443"] } }
        };
        var (_, enforcer) = Build(config, ("http_tool", NetworkFileTool()));

        var result = await enforcer.EnforceAsync(
            "http_tool", ToolCapability.FileRead | ToolCapability.NetworkAccess,
            requestedHosts: ["evil.com"]);

        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task HostScopingConfigured_RequestedHostsIsNull_RefusesFailClosed()
    {
        var config = new SandboxConfig
        {
            ToolOverrides = new() { ["http_tool"] = new ToolOverrideConfig { DeniedHosts = ["*.evil.com"] } }
        };
        var (_, enforcer) = Build(config, ("http_tool", NetworkFileTool()));

        var result = await enforcer.EnforceAsync(
            "http_tool", ToolCapability.FileRead | ToolCapability.NetworkAccess, requestedHosts: null);

        result.IsSuccess.Should().BeFalse();
    }
}
