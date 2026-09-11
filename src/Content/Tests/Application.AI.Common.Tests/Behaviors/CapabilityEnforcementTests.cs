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
        params (string Name, ITool Tool)[] tools) => Build(config, null, tools);

    private static (ToolPermissionProfileResolver Resolver, CapabilityEnforcer Enforcer) Build(
        SandboxConfig? config,
        Mock<ILogger<CapabilityEnforcer>>? logger,
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
        var enforcer = new CapabilityEnforcer(resolver, (logger ?? new Mock<ILogger<CapabilityEnforcer>>()).Object);
        return (resolver, enforcer);
    }

    private static bool LogsMessageContaining(Mock<ILogger<CapabilityEnforcer>> logger, string substring) =>
        logger.Invocations.Any(i =>
            i.Method.Name == nameof(ILogger.Log) &&
            i.Arguments.Count > 2 &&
            i.Arguments[2] is not null &&
            i.Arguments[2]!.ToString()!.Contains(substring, StringComparison.Ordinal));

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
    public async Task DeniedPath_ConfiguredEntryIsRelative_StillRefuses()
    {
        // Regression: the fully-qualified check was originally applied only to the requested path,
        // not to a configured boundary — a relative DeniedPaths entry like "sandbox/secrets" would
        // silently resolve against the host process's own working directory via PathScope.Normalize
        // and then never match any real, fully-qualified requested path, turning the deny rule into a
        // permanent no-op. Neither side of this comparison has a base directory the other would agree
        // with, so a relative config entry must fail closed exactly like a relative requested path does.
        var config = new SandboxConfig
        {
            ToolOverrides = new() { ["file_system"] = new ToolOverrideConfig { DeniedPaths = ["sandbox/secrets"] } }
        };
        var (_, enforcer) = Build(config, ("file_system", FileTool()));

        var result = await enforcer.EnforceAsync(
            "file_system", ToolCapability.FileRead | ToolCapability.FileWrite,
            requestedPaths: [$"{Root}sandbox/unrelated.txt"]);

        result.IsSuccess.Should().BeFalse("a relative deny entry must fail closed, not silently become a no-op");
    }

    [Fact]
    public async Task AllowedPath_ConfiguredEntryIsRelative_ExcludesRatherThanFalsePositiveAllow()
    {
        // The allow-side mirror of the deny regression above: a relative AllowedPaths entry must not
        // resolve against the host process's own CWD and coincidentally grant access nothing was
        // meant to grant. It should be excluded from the allowlist (never matches), which for a
        // profile whose only entry is relative means every request is refused.
        var config = new SandboxConfig
        {
            ToolOverrides = new() { ["file_system"] = new ToolOverrideConfig { AllowedPaths = ["sandbox/work"] } }
        };
        var (_, enforcer) = Build(config, ("file_system", FileTool()));

        var result = await enforcer.EnforceAsync(
            "file_system", ToolCapability.FileRead | ToolCapability.FileWrite,
            requestedPaths: [$"{Root}sandbox/work/notes.txt"]);

        result.IsSuccess.Should().BeFalse("a relative allow entry must not grant access via a CWD coincidence");
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
    public async Task DeniedPath_NestedInsideRequestedAncestor_Refuses()
    {
        // CI-caught HIGH: FileSystemTool's "search"/"list" operations recursively read everything
        // beneath the single declared "path" argument, but the enforcer only ever validated that one
        // named string. A denied boundary NESTED INSIDE the requested path (the requested path is an
        // ANCESTOR of the deny entry, not a descendant of it) previously passed unnoticed, silently
        // reading through a subdirectory the operator explicitly denied. Checked in both directions
        // now: a deny match on EITHER "requested is under denied" OR "denied is under requested".
        var config = new SandboxConfig
        {
            ToolOverrides = new()
            {
                ["file_system"] = new ToolOverrideConfig
                {
                    AllowedPaths = [$"{Root}work"],
                    DeniedPaths = [$"{Root}work/.secrets"]
                }
            }
        };
        var (_, enforcer) = Build(config, ("file_system", FileTool()));

        // A "search"/"list"-shaped call names the allowed root itself, not the denied child.
        var result = await enforcer.EnforceAsync(
            "file_system", ToolCapability.FileRead | ToolCapability.FileWrite,
            requestedPaths: [$"{Root}work"]);

        result.IsSuccess.Should().BeFalse(
            "a subtree read from an ancestor of a denied boundary must not silently read through it");
    }

    [Fact]
    public async Task DeniedPath_SiblingOfDeniedBoundary_StillAllowed()
    {
        // The mirror of the regression above: a requested path that is a SIBLING of a denied
        // boundary (neither an ancestor nor a descendant of it) must not be caught by the new
        // both-directions check — only genuine ancestor/descendant containment should refuse.
        var config = new SandboxConfig
        {
            ToolOverrides = new()
            {
                ["file_system"] = new ToolOverrideConfig
                {
                    AllowedPaths = [$"{Root}work"],
                    DeniedPaths = [$"{Root}work/.secrets"]
                }
            }
        };
        var (_, enforcer) = Build(config, ("file_system", FileTool()));

        var result = await enforcer.EnforceAsync(
            "file_system", ToolCapability.FileRead | ToolCapability.FileWrite,
            requestedPaths: [$"{Root}work/public"]);

        result.IsSuccess.Should().BeTrue("a sibling of a denied boundary is not itself denied");
    }

    [Fact]
    public async Task AllowedPath_ConfiguredEntryIsEmptyString_ExcludesRatherThanMatchingEverything()
    {
        // CI-caught MEDIUM: NormalizeBoundary's raw-string fallback on a normalization failure is
        // meant to safely EXCLUDE a malformed allow entry, but an empty/whitespace configured string
        // coincided with IsPathWithin's own "empty boundary confines everything" rule for a
        // genuinely-normalized root path, silently matching every candidate instead of none.
        var config = new SandboxConfig
        {
            ToolOverrides = new() { ["file_system"] = new ToolOverrideConfig { AllowedPaths = [""] } }
        };
        var (_, enforcer) = Build(config, ("file_system", FileTool()));

        var result = await enforcer.EnforceAsync(
            "file_system", ToolCapability.FileRead | ToolCapability.FileWrite,
            requestedPaths: [$"{Root}anywhere/at/all.txt"]);

        result.IsSuccess.Should().BeFalse("an empty allow entry must exclude, not silently match everything");
    }

    [Fact]
    public async Task DeniedPath_ConfiguredListContainsNullEntry_DoesNotThrow()
    {
        // Advisory: a literal JSON `null` in DeniedPaths binds to a null List<string> element despite
        // the non-nullable element type, and used to NRE inside NormalizeAndCanonicalize rather than
        // being treated like any other unparsable entry.
        var config = new SandboxConfig
        {
            ToolOverrides = new()
            {
                ["file_system"] = new ToolOverrideConfig { DeniedPaths = [null!, $"{Root}sandbox/secrets"] }
            }
        };
        var (_, enforcer) = Build(config, ("file_system", FileTool()));

        var act = () => enforcer.EnforceAsync(
            "file_system", ToolCapability.FileRead | ToolCapability.FileWrite,
            requestedPaths: [$"{Root}sandbox/secrets/creds.txt"]);

        (await act.Should().NotThrowAsync()).Which.IsSuccess.Should().BeFalse();
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
    public async Task DeniedHostOnly_RequestedHostIsEmptyString_Refuses()
    {
        // #595 code-review: a deny-list-only config has no AllowedHosts, so the allow-check in
        // ValidateHosts never runs — before this fix, an empty-string requested host matched no
        // configured deny pattern either, and the loop fell through with no violation, silently
        // admitting it. Mirrors ValidatePaths' unconditional rejection of an unparsable path.
        var config = new SandboxConfig
        {
            ToolOverrides = new() { ["http_tool"] = new ToolOverrideConfig { DeniedHosts = ["*.evil.com"] } }
        };
        var (_, enforcer) = Build(config, ("http_tool", NetworkFileTool()));

        var result = await enforcer.EnforceAsync(
            "http_tool", ToolCapability.FileRead | ToolCapability.NetworkAccess,
            requestedHosts: [""]);

        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task DeniedHostOnly_RequestedHostIsMalformedButNonEmpty_Refuses()
    {
        // #605: #595's fix above closed only the empty-string case of this gap. A non-empty but
        // malformed host (embedded control character) matched no configured deny pattern and fell
        // through the allow-check (which never runs in a deny-list-only config) unchecked — silently
        // admitted. SecureInputValidatorHelper.ValidateHost now rejects the whole class outright,
        // mirroring ValidatePaths' unconditional rejection of an unparsable path.
        var config = new SandboxConfig
        {
            ToolOverrides = new() { ["http_tool"] = new ToolOverrideConfig { DeniedHosts = ["*.evil.com"] } }
        };
        var (_, enforcer) = Build(config, ("http_tool", NetworkFileTool()));

        var result = await enforcer.EnforceAsync(
            "http_tool", ToolCapability.FileRead | ToolCapability.NetworkAccess,
            requestedHosts: ["evil\0.com"]);

        result.IsSuccess.Should().BeFalse();
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

    // --- #635: NormalizeHostForMatch bypasses ---

    [Theory]
    [InlineData("2130706433")] // decimal IPv4
    [InlineData("127.1")] // short-form IPv4
    [InlineData("0x7f.0.0.1")] // hex-octet IPv4
    public async Task DeniedHost_AlternateIPv4Encoding_StillMatchesDottedQuadDenyEntry(string encodedLoopback)
    {
        var config = new SandboxConfig
        {
            ToolOverrides = new() { ["http_tool"] = new ToolOverrideConfig { DeniedHosts = ["127.0.0.1"] } }
        };
        var (_, enforcer) = Build(config, ("http_tool", NetworkFileTool()));

        var result = await enforcer.EnforceAsync(
            "http_tool", ToolCapability.FileRead | ToolCapability.NetworkAccess,
            requestedHosts: [encodedLoopback]);

        result.IsSuccess.Should().BeFalse(
            $"'{encodedLoopback}' is the same address as the denied 127.0.0.1 to the real HTTP client");
    }

    [Theory]
    [InlineData("evil。com")] // U+3002 ideographic full stop
    [InlineData("evil．com")] // U+FF0E fullwidth full stop
    [InlineData("evil.com｡")] // U+FF61 halfwidth ideographic full stop (trailing)
    [InlineData("evil.com​")] // trailing zero-width space
    public async Task DeniedHost_UnicodeLabelSeparatorOrZeroWidthChar_StillMatchesAsciiDenyEntry(string spoofedHost)
    {
        var config = new SandboxConfig
        {
            ToolOverrides = new() { ["http_tool"] = new ToolOverrideConfig { DeniedHosts = ["evil.com"] } }
        };
        var (_, enforcer) = Build(config, ("http_tool", NetworkFileTool()));

        var result = await enforcer.EnforceAsync(
            "http_tool", ToolCapability.FileRead | ToolCapability.NetworkAccess,
            requestedHosts: [spoofedHost]);

        result.IsSuccess.Should().BeFalse(
            "Uri.IdnHost — what the real HTTP client connects with — normalizes this to plain \"evil.com\"");
    }

    [Fact]
    public async Task DeniedHost_UnicodeLabelSeparator_StillDefeatsWildcardDenyEntry()
    {
        var config = new SandboxConfig
        {
            ToolOverrides = new() { ["http_tool"] = new ToolOverrideConfig { DeniedHosts = ["*.evil.com"] } }
        };
        var (_, enforcer) = Build(config, ("http_tool", NetworkFileTool()));

        var result = await enforcer.EnforceAsync(
            "http_tool", ToolCapability.FileRead | ToolCapability.NetworkAccess,
            requestedHosts: ["a.evil。com"]);

        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task DeniedHost_BracketedIPv6Literal_StillMatchesBareDenyEntry()
    {
        // Before #635: the absolute-URI branch of NormalizeHostForMatch returned Uri.Host verbatim,
        // which retains brackets ("[::1]") — a bare deny entry of "::1" never matched a requested
        // "http://[::1]/".
        var config = new SandboxConfig
        {
            ToolOverrides = new() { ["http_tool"] = new ToolOverrideConfig { DeniedHosts = ["::1"] } }
        };
        var (_, enforcer) = Build(config, ("http_tool", NetworkFileTool()));

        var result = await enforcer.EnforceAsync(
            "http_tool", ToolCapability.FileRead | ToolCapability.NetworkAccess,
            requestedHosts: ["http://[::1]/"]);

        result.IsSuccess.Should().BeFalse();
    }

    [Theory]
    [InlineData("fe80::1%eth0")]
    [InlineData("[fe80::1%25eth0]")]
    public async Task DeniedHost_IPv6ZoneIdentifier_StillMatchesBareDenyEntryWithoutZone(string zoneQualifiedHost)
    {
        var config = new SandboxConfig
        {
            ToolOverrides = new() { ["http_tool"] = new ToolOverrideConfig { DeniedHosts = ["fe80::1"] } }
        };
        var (_, enforcer) = Build(config, ("http_tool", NetworkFileTool()));

        var result = await enforcer.EnforceAsync(
            "http_tool", ToolCapability.FileRead | ToolCapability.NetworkAccess,
            requestedHosts: [zoneQualifiedHost]);

        result.IsSuccess.Should().BeFalse(
            "a bare vs. percent-escaped zone id spelling of the same interface must normalize identically");
    }

    [Fact]
    public async Task AllowedHost_UnrelatedPrefixCollisionHost_StillRefused()
    {
        // Guard case: the #635 fix must not become OVER-broad — "notevil.com" must never be treated
        // as if it were "evil.com" just because #635's synthetic-scheme parsing now runs on more
        // bare-shaped values than before.
        var config = new SandboxConfig
        {
            ToolOverrides = new() { ["http_tool"] = new ToolOverrideConfig { AllowedHosts = ["evil.com"] } }
        };
        var (_, enforcer) = Build(config, ("http_tool", NetworkFileTool()));

        var result = await enforcer.EnforceAsync(
            "http_tool", ToolCapability.FileRead | ToolCapability.NetworkAccess,
            requestedHosts: ["notevil.com"]);

        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task AllowedHost_PunycodeLookalike_DoesNotCollideWithUnicodeDenyEntry()
    {
        // Guard case: a value that is ALREADY in ASCII/punycode form must not be treated as if
        // IdnHost-canonicalizing it could make it collide with an unrelated Unicode-derived host.
        var config = new SandboxConfig
        {
            ToolOverrides = new() { ["http_tool"] = new ToolOverrideConfig { AllowedHosts = ["evil.com"] } }
        };
        var (_, enforcer) = Build(config, ("http_tool", NetworkFileTool()));

        var result = await enforcer.EnforceAsync(
            "http_tool", ToolCapability.FileRead | ToolCapability.NetworkAccess,
            requestedHosts: ["xn--vil-9ma.com"]);

        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task DeniedHost_MalformedConfiguredEntry_LogsInertConfigurationWarning()
    {
        // #635 LOW: a typo'd deny entry that can never match any requested host previously failed
        // silently, with no signal to the operator that their configuration does nothing.
        var config = new SandboxConfig
        {
            ToolOverrides = new()
            {
                ["http_tool"] = new ToolOverrideConfig { DeniedHosts = ["evil.com/*"] }
            }
        };
        var loggerMock = new Mock<ILogger<CapabilityEnforcer>>();
        var (_, enforcer) = Build(config, loggerMock, ("http_tool", NetworkFileTool()));

        await enforcer.EnforceAsync(
            "http_tool", ToolCapability.FileRead | ToolCapability.NetworkAccess,
            requestedHosts: ["example.com"]);

        LogsMessageContaining(loggerMock, "can never match any requested host").Should().BeTrue();
    }

    [Fact]
    public async Task DeniedHost_WellFormedConfiguredEntry_DoesNotLogInertConfigurationWarning()
    {
        var config = new SandboxConfig
        {
            ToolOverrides = new()
            {
                ["http_tool"] = new ToolOverrideConfig { DeniedHosts = ["evil.com"] } // well-formed
            }
        };
        var loggerMock = new Mock<ILogger<CapabilityEnforcer>>();
        var (_, enforcer) = Build(config, loggerMock, ("http_tool", NetworkFileTool()));

        await enforcer.EnforceAsync(
            "http_tool", ToolCapability.FileRead | ToolCapability.NetworkAccess,
            requestedHosts: ["example.com"]);

        LogsMessageContaining(loggerMock, "can never match any requested host").Should().BeFalse();
    }

    [Theory]
    [InlineData("::ffff:127.0.0.1")] // IPv4-mapped IPv6, colon-hex form
    [InlineData("[::ffff:127.0.0.1]")] // same, bracketed as a URI would carry it
    public async Task DeniedHost_Ipv4MappedIpv6Literal_StillMatchesPlainIPv4DenyEntry(string mappedForm)
    {
        // #635 code-review (round 2): a well-formed IPv6 literal encoding the SAME address as a
        // plain IPv4 deny entry — Uri.IdnHost does not collapse this form, so it needs its own
        // explicit normalization step (mirrors CompositeHookExecutor.IsReservedAddress).
        var config = new SandboxConfig
        {
            ToolOverrides = new() { ["http_tool"] = new ToolOverrideConfig { DeniedHosts = ["127.0.0.1"] } }
        };
        var (_, enforcer) = Build(config, ("http_tool", NetworkFileTool()));

        var result = await enforcer.EnforceAsync(
            "http_tool", ToolCapability.FileRead | ToolCapability.NetworkAccess,
            requestedHosts: [mappedForm]);

        result.IsSuccess.Should().BeFalse(
            $"'{mappedForm}' is the same address as the denied 127.0.0.1 to the real HTTP client");
    }

    [Fact]
    public async Task DeniedHost_Ipv4MappedIpv6CloudMetadataAddress_StillMatchesDenyEntry()
    {
        // The concrete exploit shape from #635's own issue text, restated for the IPv6-mapped form.
        var config = new SandboxConfig
        {
            ToolOverrides = new() { ["http_tool"] = new ToolOverrideConfig { DeniedHosts = ["169.254.169.254"] } }
        };
        var (_, enforcer) = Build(config, ("http_tool", NetworkFileTool()));

        var result = await enforcer.EnforceAsync(
            "http_tool", ToolCapability.FileRead | ToolCapability.NetworkAccess,
            requestedHosts: ["::ffff:169.254.169.254"]);

        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task AllowedHost_UnrelatedIpv6_DoesNotCollideWithIpv4MappedNormalization()
    {
        // Guard case: IPv4-mapped-IPv6 normalization must not over-fire on an ordinary IPv6 address
        // that is NOT an IPv4-mapped literal.
        var config = new SandboxConfig
        {
            ToolOverrides = new() { ["http_tool"] = new ToolOverrideConfig { AllowedHosts = ["127.0.0.1"] } }
        };
        var (_, enforcer) = Build(config, ("http_tool", NetworkFileTool()));

        var result = await enforcer.EnforceAsync(
            "http_tool", ToolCapability.FileRead | ToolCapability.NetworkAccess,
            requestedHosts: ["2001:db8::1"]);

        result.IsSuccess.Should().BeFalse("an unrelated IPv6 address must not be treated as 127.0.0.1");
    }

    [Theory]
    [InlineData("evil.com@allowed.com")] // userinfo
    [InlineData("allowed.com#evil.com")] // fragment
    [InlineData("allowed.com?evil.com")] // query
    [InlineData("allowed.com\\evil.com")] // backslash
    public async Task DeniedHostOnly_BareValueWithUserinfoFragmentQueryOrBackslash_StillRefused(string ambiguousValue)
    {
        // run-gates correctness/security review: these shapes were refused outright as malformed
        // before #635 (Uri.CheckHostName rejects them). #635's synthetic-scheme parsing must not
        // start silently reducing them to just the leading host segment — kept excluded alongside
        // '/' so this file's own normalization never disagrees with a consumer that isn't Uri/
        // HttpClient (a raw socket, a DNS lookup, a subprocess) about which host a value names.
        var config = new SandboxConfig
        {
            ToolOverrides = new() { ["http_tool"] = new ToolOverrideConfig { DeniedHosts = ["*.evil.com"] } }
        };
        var (_, enforcer) = Build(config, ("http_tool", NetworkFileTool()));

        var result = await enforcer.EnforceAsync(
            "http_tool", ToolCapability.FileRead | ToolCapability.NetworkAccess,
            requestedHosts: [ambiguousValue]);

        result.IsSuccess.Should().BeFalse($"'{ambiguousValue}' must stay refused as malformed, not silently reduced to a bare host");
    }

    [Theory]
    [InlineData("::127.0.0.1")] // deprecated IPv4-compatible form, expanded
    [InlineData("::7f00:1")] // same address, compressed hex form
    public async Task DeniedHost_DeprecatedIpv4CompatibleIpv6Literal_StillMatchesPlainIPv4DenyEntry(string compatibleForm)
    {
        // #635 round-2 code-review: distinct from the IPv4-MAPPED form ("::ffff:a.b.c.d") already
        // covered above — this is the older, RFC 4291-deprecated "IPv4-compatible" form (no "ffff"),
        // which IPAddress.IsIPv4MappedToIPv6 does NOT recognize.
        var config = new SandboxConfig
        {
            ToolOverrides = new() { ["http_tool"] = new ToolOverrideConfig { DeniedHosts = ["127.0.0.1"] } }
        };
        var (_, enforcer) = Build(config, ("http_tool", NetworkFileTool()));

        var result = await enforcer.EnforceAsync(
            "http_tool", ToolCapability.FileRead | ToolCapability.NetworkAccess,
            requestedHosts: [compatibleForm]);

        result.IsSuccess.Should().BeFalse(
            $"'{compatibleForm}' is the same address as the denied 127.0.0.1 to the real HTTP client");
    }

    [Fact]
    public async Task AllowedHost_Ipv6Loopback_IsNotMisidentifiedAsAnUnrelatedIPv4Address()
    {
        // Guard case: naively taking the last 4 bytes of "::1" (loopback) gives "0.0.0.1", NOT
        // "127.0.0.1" — the IPv4-compatible-form collapse must exclude loopback/unspecified rather
        // than blindly treating any all-zero-prefixed IPv6 address as IPv4-compatible. Configuring
        // the WRONG value ("0.0.0.1") the naive bug would produce, rather than the correct one
        // ("127.0.0.1"), so this test actually discriminates: refusing "::1" against a
        // "127.0.0.1" allow entry is ALSO the correct outcome, just for a different reason,
        // so that pairing can't tell a fixed collapse from a differently-broken one.
        var config = new SandboxConfig
        {
            ToolOverrides = new() { ["http_tool"] = new ToolOverrideConfig { AllowedHosts = ["0.0.0.1"] } }
        };
        var (_, enforcer) = Build(config, ("http_tool", NetworkFileTool()));

        var result = await enforcer.EnforceAsync(
            "http_tool", ToolCapability.FileRead | ToolCapability.NetworkAccess,
            requestedHosts: ["::1"]);

        result.IsSuccess.Should().BeFalse("\"::1\" (loopback) must not be collapsed into an unrelated \"0.0.0.1\"");
    }

    [Fact]
    public async Task DeniedHostOnly_RequestedHostTriggersIdnHostException_StillRefused()
    {
        // #635 round-2 code-review: verified live that Uri.IdnHost throws UriFormatException for a
        // mixed valid-character-plus-invalid-Unicode label — the fail-closed sentinel path
        // (CanonicalizeParsedHost's catch block) must refuse the call, not silently admit it.
        var config = new SandboxConfig
        {
            ToolOverrides = new() { ["http_tool"] = new ToolOverrideConfig { DeniedHosts = ["*.evil.com"] } }
        };
        var (_, enforcer) = Build(config, ("http_tool", NetworkFileTool()));

        var result = await enforcer.EnforceAsync(
            "http_tool", ToolCapability.FileRead | ToolCapability.NetworkAccess,
            requestedHosts: ["a￿.com"]);

        result.IsSuccess.Should().BeFalse();
    }
}
