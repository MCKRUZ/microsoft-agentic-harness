using Application.AI.Common.Interfaces.Tools;
using Application.AI.Common.Services.Tools;
using Domain.AI.Models;
using FluentAssertions;
using Infrastructure.AI.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Tools;

/// <summary>
/// Pins <see cref="EvidenceReaderTool"/> (#843) against a real sandboxed file service: it reads, lists and
/// searches one evidence tree, pages long files instead of clipping them, refuses anything outside the
/// tree, and has no operation that writes. The last point is the reason the tool exists: a skill's
/// declared operations are not enforced when a tool is called, so the only dependable read-only grant is a
/// tool that cannot write.
/// </summary>
public sealed class EvidenceReaderToolTests : IDisposable
{
    private readonly string _root;
    private readonly string _outside;
    private readonly EvidenceReaderTool _sut;

    public EvidenceReaderToolTests()
    {
        var parent = Path.Combine(Path.GetTempPath(), $"evidence-reader-tests-{Guid.NewGuid():N}");
        _root = Path.Combine(parent, "evidence");
        _outside = Path.Combine(parent, "secrets");
        Directory.CreateDirectory(Path.Combine(_root, "rollouts"));
        Directory.CreateDirectory(_outside);
        File.WriteAllText(Path.Combine(_outside, "token.txt"), "do-not-read-me");
        _sut = new EvidenceReaderTool(new FileSystemService(NullLogger<FileSystemService>.Instance, [_root]));
    }

    public void Dispose() => Directory.Delete(Path.GetDirectoryName(_root)!, recursive: true);

    private static Dictionary<string, object?> Args(params (string Key, object? Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => p.Value);

    /// <summary>Calls the tool as a proposer session would: with this step's folder (the root here) published.</summary>
    private async Task<ToolResult> Run(string operation, Dictionary<string, object?> args)
    {
        using var _ = EvidenceReadScope.Begin(_root);
        return await _sut.ExecuteAsync(operation, args);
    }

    [Fact]
    public async Task Execute_WithNoStepFolderPublished_RefusesEvenAPathInsideTheRoot()
    {
        var path = Path.Combine(_root, "rollouts", "001-a.md");
        await File.WriteAllTextAsync(path, "private trajectory");

        var result = await _sut.ExecuteAsync("read", Args(("path", path)));

        result.Success.Should().BeFalse();
        result.Output.Should().NotContain("private trajectory");
    }

    [Fact]
    public async Task Execute_AnotherStepsFolderInTheSameRoot_IsRefused()
    {
        var mine = Path.Combine(_root, "step-1");
        var theirs = Path.Combine(_root, "step-10");
        Directory.CreateDirectory(mine);
        Directory.CreateDirectory(theirs);
        await File.WriteAllTextAsync(Path.Combine(mine, "a.md"), "mine");
        await File.WriteAllTextAsync(Path.Combine(theirs, "a.md"), "another run's trajectory");
        using var _ = EvidenceReadScope.Begin(mine);

        var own = await _sut.ExecuteAsync("read", Args(("path", Path.Combine(mine, "a.md"))));
        var sibling = await _sut.ExecuteAsync("read", Args(("path", Path.Combine(theirs, "a.md"))));
        var listing = await _sut.ExecuteAsync("list", Args(("path", _root)));
        var search = await _sut.ExecuteAsync("search", Args(("path", _root), ("search_term", "trajectory")));

        own.Output.Should().Be("mine");
        sibling.Success.Should().BeFalse();
        listing.Success.Should().BeFalse("the root lists every step");
        search.Success.Should().BeFalse("a search from the root would reach every step");
        sibling.Output.Should().NotContain("another run's trajectory");
    }

    [Fact]
    public void Shape_IsReadOnlyAndHasNoWritingOperation()
    {
        _sut.IsReadOnly.Should().BeTrue();
        _sut.IsDirectlyInvocable.Should().BeFalse("the evidence is for the proposer session, not for direct callers");
        _sut.SupportedOperations.Should().BeEquivalentTo("read", "list", "search", "exists");
        _sut.SupportedOperations.Should().NotContain("write");
    }

    [Fact]
    public async Task Execute_WriteOperation_IsRefusedAndNothingIsWritten()
    {
        var path = Path.Combine(_root, "planted.md");

        var result = await Run("write", Args(("path", path), ("content", "x")));

        result.Success.Should().BeFalse();
        File.Exists(path).Should().BeFalse();
    }

    [Fact]
    public async Task Execute_Read_ReturnsTheFile()
    {
        var path = Path.Combine(_root, "rollouts", "001-a.md");
        await File.WriteAllTextAsync(path, "passed: false\nthe agent timed out");

        var result = await Run("read", Args(("path", path)));

        result.Success.Should().BeTrue();
        result.Output.Should().Be("passed: false\nthe agent timed out");
    }

    [Fact]
    public async Task Execute_ReadOfALongFile_PagesAndSaysWhereToContinueInsteadOfClipping()
    {
        var path = Path.Combine(_root, "rollouts", "long.md");
        var text = new string('a', 100) + new string('b', 100) + new string('c', 50);
        await File.WriteAllTextAsync(path, text);

        var first = await Run("read", Args(("path", path), ("max_chars", 100)));
        var second = await Run("read", Args(("path", path), ("offset", 100), ("max_chars", 100)));
        var last = await Run("read", Args(("path", path), ("offset", 200), ("max_chars", 100)));

        first.Output.Should().StartWith(new string('a', 100)).And.Contain("150 more characters").And.Contain("offset=100");
        second.Output.Should().StartWith(new string('b', 100)).And.Contain("50 more characters").And.Contain("offset=200");
        last.Output.Should().Be(new string('c', 50), "the last page ends the file and carries no continuation note");
    }

    [Fact]
    public async Task Execute_ReadOutsideTheTree_IsRefused()
    {
        var result = await Run("read", Args(("path", Path.Combine(_outside, "token.txt"))));

        result.Success.Should().BeFalse();
        result.Output.Should().NotContain("do-not-read-me");
    }

    [Fact]
    public async Task Execute_ReadThroughDotDotOutOfTheTree_IsRefused()
    {
        var sneaky = Path.Combine(_root, "..", "secrets", "token.txt");

        var result = await Run("read", Args(("path", sneaky)));

        result.Success.Should().BeFalse();
        result.Output.Should().NotContain("do-not-read-me");
    }

    [Fact]
    public async Task Execute_ListAndSearch_WorkInsideTheTree()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "rollouts", "001-a.md"), "alpha\nneedle here\nomega");

        var list = await Run("list", Args(("path", Path.Combine(_root, "rollouts"))));
        var search = await Run("search", Args(("path", _root), ("search_term", "needle")));

        list.Output.Should().Contain("001-a.md");
        search.Output.Should().Contain("001-a.md").And.Contain("needle here");
    }

    public static TheoryData<string> UnusableResourceParameterForms => new()
    {
        "omitted", "null", "empty", "whitespace", "number", "boolean",
    };

    [Theory]
    [MemberData(nameof(UnusableResourceParameterForms))]
    public async Task Execute_DeclaredResourceParameterMissingOrUnusable_FailsAndTouchesNothing(string form)
    {
        // The contract behind #825, pinned for this tool as it is for the file tool: the extractor reads an
        // omitted or null declared path as "the call did not ask for one" and scoping passes it, which is
        // safe only while the tool FAILS rather than defaulting somewhere.
        var evidence = new Mock<IFileSystemService>();
        var sut = new EvidenceReaderTool(evidence.Object);

        foreach (var (operation, declared) in sut.ResourceParametersByOperation)
        {
            foreach (var key in declared.Keys)
            {
                var parameters = new Dictionary<string, object?> { ["search_term"] = "x" };
                switch (form)
                {
                    case "null": parameters[key] = null; break;
                    case "empty": parameters[key] = ""; break;
                    case "whitespace": parameters[key] = "   "; break;
                    case "number": parameters[key] = 42L; break;
                    case "boolean": parameters[key] = true; break;
                }

                var result = await sut.ExecuteAsync(operation, parameters);

                result.Success.Should().BeFalse($"'{operation}' with '{key}' {form} must fail, not default");
            }
        }

        evidence.Invocations.Should().BeEmpty("a call whose path is unusable must never reach the file service");
    }

    [Fact]
    public void ResourceParametersByOperation_CoversEveryOperation()
    {
        _sut.ResourceParametersByOperation.Keys.Should().BeEquivalentTo(_sut.SupportedOperations);
    }

    [Fact]
    public async Task Execute_MissingPath_FailsWithoutThrowing()
    {
        var result = await Run("read", Args());

        result.Success.Should().BeFalse();
    }
}
