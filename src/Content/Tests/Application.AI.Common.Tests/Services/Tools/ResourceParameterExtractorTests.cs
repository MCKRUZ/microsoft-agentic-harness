using Application.AI.Common.Services.Tools;
using Domain.AI.Sandbox;
using FluentAssertions;

namespace Application.AI.Common.Tests.Services.Tools;

/// <summary>
/// #418: <see cref="ResourceParameterExtractor.Extract"/> is the one routine that turns a tool's
/// <c>ResourceParametersByOperation</c> declaration plus one call's parameters into a
/// <see cref="ToolCallResourceRequest"/> — and its null/empty/populated three-way return is exactly
/// what <c>CapabilityEnforcer</c> distinguishes fail-closed behavior on.
/// </summary>
public sealed class ResourceParameterExtractorTests
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, ResourceParameterKind>> FileSystemMap =
        new Dictionary<string, IReadOnlyDictionary<string, ResourceParameterKind>>
        {
            ["read"] = new Dictionary<string, ResourceParameterKind> { ["path"] = ResourceParameterKind.Path },
            ["write"] = new Dictionary<string, ResourceParameterKind> { ["path"] = ResourceParameterKind.Path },
        };

    [Fact]
    public void Extract_ToolDeclaresNothing_ReturnsNull()
    {
        var result = ResourceParameterExtractor.Extract(
            "read", new Dictionary<string, object?> { ["path"] = "src" }, resourceParametersByOperation: null);

        result.Should().BeNull();
    }

    [Fact]
    public void Extract_ToolDeclaresNothing_EmptyMap_ReturnsNull()
    {
        var result = ResourceParameterExtractor.Extract(
            "read", new Dictionary<string, object?> { ["path"] = "src" },
            new Dictionary<string, IReadOnlyDictionary<string, ResourceParameterKind>>());

        result.Should().BeNull();
    }

    [Fact]
    public void Extract_OperationIsNull_ReturnsNull()
    {
        var result = ResourceParameterExtractor.Extract(
            operation: null, new Dictionary<string, object?> { ["path"] = "src" }, FileSystemMap);

        result.Should().BeNull();
    }

    [Fact]
    public void Extract_OperationNotDeclared_ReturnsNull()
    {
        // Regression (#587 code-review): "exists" is a real file_system operation but not one of
        // FileSystemMap's two declared operations here — the map has no entry for it at all, which is
        // exactly as unknown as a missing operation string, NOT "affirmatively declares nothing scoped
        // for it" (that's Extract_DeclaredOperationWithNoParameters_ReturnsEmpty below, where the map
        // DOES have an entry). Conflating the two — this test's own original, incorrect expectation —
        // let an operation the tool couldn't recognize at all (including a corrupted/garbled operation
        // string arriving from a plan's upstream-output merge) skip scoping entirely.
        var result = ResourceParameterExtractor.Extract(
            "exists", new Dictionary<string, object?> { ["path"] = "src" }, FileSystemMap);

        result.Should().BeNull();
    }

    [Fact]
    public void Extract_OperationCasingDiffers_StillMatchesDeclaration()
    {
        // Regression: every other stage that admits an operation name (AIToolConverter,
        // DirectToolInvoker) accepts it case-insensitively, and FileSystemTool.ExecuteAsync itself
        // dispatches via ToLowerInvariant() — an ordinal-only lookup here would silently downgrade
        // "Read" to "affirmatively nothing scoped" (Empty), which CapabilityEnforcer trusts and skips
        // validating, bypassing scoping entirely for a call whose casing merely differs.
        var result = ResourceParameterExtractor.Extract(
            "Read", new Dictionary<string, object?> { ["path"] = "src/File.cs" }, FileSystemMap);

        result.Should().NotBeNull();
        result!.RequestedPaths.Should().ContainSingle().Which.Should().Be("src/File.cs");
    }

    [Fact]
    public void Extract_DeclaredOperationWithNoParameters_ReturnsEmpty()
    {
        var map = new Dictionary<string, IReadOnlyDictionary<string, ResourceParameterKind>>
        {
            ["list"] = new Dictionary<string, ResourceParameterKind>()
        };

        var result = ResourceParameterExtractor.Extract(
            "list", new Dictionary<string, object?> { ["path"] = "src" }, map);

        result.Should().Be(ToolCallResourceRequest.Empty);
    }

    [Fact]
    public void Extract_DeclaredPathParameterPresent_ReturnsIt()
    {
        var result = ResourceParameterExtractor.Extract(
            "read", new Dictionary<string, object?> { ["path"] = "src/File.cs" }, FileSystemMap);

        result.Should().NotBeNull();
        result!.RequestedPaths.Should().ContainSingle().Which.Should().Be("src/File.cs");
        result.RequestedHosts.Should().BeEmpty();
    }

    [Fact]
    public void Extract_DeclaredHostParameterPresent_ReturnsIt()
    {
        var map = new Dictionary<string, IReadOnlyDictionary<string, ResourceParameterKind>>
        {
            ["fetch"] = new Dictionary<string, ResourceParameterKind> { ["url_host"] = ResourceParameterKind.Host }
        };

        var result = ResourceParameterExtractor.Extract(
            "fetch", new Dictionary<string, object?> { ["url_host"] = "example.com" }, map);

        result.Should().NotBeNull();
        result!.RequestedHosts.Should().ContainSingle().Which.Should().Be("example.com");
        result.RequestedPaths.Should().BeEmpty();
    }

    [Fact]
    public void Extract_DeclaredParameterMissingFromCall_ReturnsEmptyRequest()
    {
        // The declared key isn't in this call's parameters at all — not "couldn't determine",
        // since the operation itself IS declared; this call just didn't supply the parameter.
        var result = ResourceParameterExtractor.Extract(
            "read", new Dictionary<string, object?>(), FileSystemMap);

        result.Should().NotBeNull();
        result!.RequestedPaths.Should().BeEmpty();
    }

    [Fact]
    public void Extract_DeclaredParameterIsNonStringValue_IsIgnored()
    {
        var result = ResourceParameterExtractor.Extract(
            "read", new Dictionary<string, object?> { ["path"] = 42L }, FileSystemMap);

        result.Should().NotBeNull();
        result!.RequestedPaths.Should().BeEmpty();
    }

    [Fact]
    public void Extract_DeclaredParameterIsEmptyString_IsIncludedNotIgnored()
    {
        // #595 code-review: an empty string is a present-but-invalid path value, not an absent one.
        // Excluding it here (as the non-string case above correctly does) would make RequestedPaths
        // empty, and CapabilityEnforcer treats an empty request as "nothing to check" and passes it —
        // silently bypassing scoping. Including it lets path validation deny it as unparsable instead.
        var result = ResourceParameterExtractor.Extract(
            "read", new Dictionary<string, object?> { ["path"] = "" }, FileSystemMap);

        result.Should().NotBeNull();
        result!.RequestedPaths.Should().ContainSingle().Which.Should().BeEmpty();
    }

    [Fact]
    public void Extract_DeclaredHostParameterIsEmptyString_IsIncludedNotIgnored()
    {
        // Symmetric with the Path case above (round-6 code-review): the same present-but-invalid
        // reasoning applies to a declared Host parameter, not just Path.
        var map = new Dictionary<string, IReadOnlyDictionary<string, ResourceParameterKind>>
        {
            ["fetch"] = new Dictionary<string, ResourceParameterKind> { ["url_host"] = ResourceParameterKind.Host }
        };

        var result = ResourceParameterExtractor.Extract(
            "fetch", new Dictionary<string, object?> { ["url_host"] = "" }, map);

        result.Should().NotBeNull();
        result!.RequestedHosts.Should().ContainSingle().Which.Should().BeEmpty();
    }

    [Fact]
    public void Extract_ParametersIsNullButOperationIsDeclared_ReturnsEmptyRequest()
    {
        var result = ResourceParameterExtractor.Extract("read", parameters: null, FileSystemMap);

        result.Should().NotBeNull();
        result!.RequestedPaths.Should().BeEmpty();
    }
}
