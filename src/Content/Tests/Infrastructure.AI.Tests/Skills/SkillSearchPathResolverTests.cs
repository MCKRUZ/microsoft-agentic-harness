using Application.AI.Common.Exceptions;
using Application.AI.Common.Interfaces.Skills;
using Domain.Common.Config.AI;
using FluentAssertions;
using Infrastructure.AI.Skills;
using Microsoft.Extensions.Logging.Abstractions;
using Tests.Common;
using Xunit;

namespace Infrastructure.AI.Tests.Skills;

/// <summary>
/// Unit tests for <see cref="SkillSearchPathResolver"/>, mirroring
/// <c>Agents.AgentSearchPathResolverTests</c> from issue #705.
/// </summary>
public sealed class SkillSearchPathResolverTests
{
    [Fact]
    public void Resolve_OneEntryIsMalformed_SkipsItAndStillResolvesTheValidOnes()
    {
        var validPath = Path.Combine(Path.GetTempPath(), $"skills-resolver-valid-{Guid.NewGuid():N}");
        Directory.CreateDirectory(validPath);
        try
        {
            var config = new SkillsConfig
            {
                BasePath = validPath,
                AdditionalPaths = ["\0not-a-valid-path"]
            };

            var resolved = SkillSearchPathResolver.Resolve(config, new UnsandboxedSkillFileReader(), NullLogger.Instance);

            resolved.Should().ContainSingle().Which.Should().Be(validPath);
        }
        finally
        {
            Directory.Delete(validPath, recursive: true);
        }
    }

    [Fact]
    public void Resolve_NoPathsConfigured_ReturnsEmpty()
    {
        var resolved = SkillSearchPathResolver.Resolve(
            new SkillsConfig(), new UnsandboxedSkillFileReader(), NullLogger.Instance);

        resolved.Should().BeEmpty();
    }

    [Fact]
    public void Resolve_NullConfig_ReturnsEmpty()
    {
        var resolved = SkillSearchPathResolver.Resolve(null, new UnsandboxedSkillFileReader(), NullLogger.Instance);

        resolved.Should().BeEmpty();
    }

    [Fact]
    public void Resolve_ConfiguredPathDoesNotExist_SkipsItWithoutThrowing()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), $"skills-resolver-missing-{Guid.NewGuid():N}");

        var resolved = SkillSearchPathResolver.Resolve(
            new SkillsConfig { BasePath = missingPath }, new UnsandboxedSkillFileReader(), NullLogger.Instance);

        resolved.Should().BeEmpty();
    }

    [Fact]
    public void Resolve_ChecksExistenceThroughTheSandboxedReader_NotRawDirectoryExists()
    {
        // The deliberate difference from AgentSearchPathResolver: this resolver must route through
        // ISkillFileReader (issue #247's sandbox), never raw Directory.Exists.
        var realPath = Path.Combine(Path.GetTempPath(), $"skills-resolver-sandboxed-{Guid.NewGuid():N}");
        Directory.CreateDirectory(realPath);
        try
        {
            var reader = new AlwaysRefusingReader();

            var act = () => SkillSearchPathResolver.Resolve(
                new SkillsConfig { BasePath = realPath }, reader, NullLogger.Instance);

            act.Should().Throw<SkillPathRefusedException>(
                "the resolver must consult the sandboxed reader for existence, not bypass it via raw Directory.Exists");
        }
        finally
        {
            Directory.Delete(realPath, recursive: true);
        }
    }

    private sealed class AlwaysRefusingReader : ISkillFileReader
    {
        public string ReadText(string path) => throw new SkillPathRefusedException(path);
        public Task<string> ReadTextAsync(string path, CancellationToken cancellationToken = default) =>
            throw new SkillPathRefusedException(path);
        public bool FileExists(string path) => throw new SkillPathRefusedException(path);
        public bool DirectoryExists(string path) => throw new SkillPathRefusedException(path);
        public IReadOnlyList<string> EnumerateDirectories(string path) => throw new SkillPathRefusedException(path);
    }
}
