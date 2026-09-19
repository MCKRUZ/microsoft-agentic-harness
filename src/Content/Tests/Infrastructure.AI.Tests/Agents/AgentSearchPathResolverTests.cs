using Domain.Common.Config.AI;
using FluentAssertions;
using Infrastructure.AI.Agents;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Infrastructure.AI.Tests.Agents;

/// <summary>
/// Unit tests for <see cref="AgentSearchPathResolver"/>, split out of
/// <see cref="AgentMetadataRegistryTests"/> so the resolver's own contract — including the
/// malformed-path hardening added on code review for issue #705 — is verified independently of the
/// full registry.
/// </summary>
public sealed class AgentSearchPathResolverTests
{
    [Fact]
    public void Resolve_OneEntryIsMalformed_SkipsItAndStillResolvesTheValidOnes()
    {
        // Code-review finding on #705: Path.GetFullPath is not guaranteed to succeed for an arbitrary
        // configured string (an embedded NUL is rejected on every supported platform). Before this
        // fix, one bad AdditionalPaths entry would throw out of Resolve entirely, aborting discovery
        // of every OTHER configured path too — the same "one bad entry breaks everything" failure
        // shape the existing not-found branch already guards against for a merely-missing directory.
        var validPath = Path.Combine(Path.GetTempPath(), $"agents-resolver-valid-{Guid.NewGuid():N}");
        Directory.CreateDirectory(validPath);
        try
        {
            var config = new AgentsConfig
            {
                BasePath = validPath,
                AdditionalPaths = ["\0not-a-valid-path"]
            };

            var resolved = AgentSearchPathResolver.Resolve(config, NullLogger.Instance);

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
        var resolved = AgentSearchPathResolver.Resolve(new AgentsConfig(), NullLogger.Instance);

        resolved.Should().BeEmpty();
    }

    [Fact]
    public void Resolve_NullConfig_ReturnsEmpty()
    {
        var resolved = AgentSearchPathResolver.Resolve(null, NullLogger.Instance);

        resolved.Should().BeEmpty();
    }

    [Fact]
    public void Resolve_ConfiguredPathDoesNotExist_SkipsItWithoutThrowing()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), $"agents-resolver-missing-{Guid.NewGuid():N}");

        var resolved = AgentSearchPathResolver.Resolve(
            new AgentsConfig { BasePath = missingPath }, NullLogger.Instance);

        resolved.Should().BeEmpty();
    }
}
