using FluentAssertions;
using Infrastructure.AI.Sandbox;
using Xunit;

namespace Infrastructure.AI.Tests.Sandbox;

/// <summary>
/// Coverage for <see cref="SandboxWorkspace.SeedFrom"/> — #371's copy (never link/mount) of a
/// caller-supplied directory (e.g. a bundle's staged files) into a fresh sandbox workspace.
/// </summary>
public class SandboxWorkspaceSeedFromTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"seed-from-tests-{Guid.NewGuid():N}");

    public SandboxWorkspaceSeedFromTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void SeedFrom_RelativeSource_Throws()
    {
        var destination = Path.Combine(_root, "dest");
        Directory.CreateDirectory(destination);

        var act = () => SandboxWorkspace.SeedFrom("relative/path", destination);

        act.Should().Throw<ArgumentException>().WithMessage("*absolute*");
    }

    [Fact]
    public void SeedFrom_MissingSource_Throws()
    {
        var destination = Path.Combine(_root, "dest");
        Directory.CreateDirectory(destination);
        var missingSource = Path.Combine(_root, "does-not-exist");

        var act = () => SandboxWorkspace.SeedFrom(missingSource, destination);

        act.Should().Throw<DirectoryNotFoundException>();
    }

    [Fact]
    public void SeedFrom_FilesAndNestedDirectories_AreCopiedToDestination()
    {
        var source = Path.Combine(_root, "source");
        Directory.CreateDirectory(Path.Combine(source, "nested"));
        File.WriteAllText(Path.Combine(source, "server.js"), "console.log('hi');");
        File.WriteAllText(Path.Combine(source, "nested", "data.json"), "{}");
        var destination = Path.Combine(_root, "dest");
        Directory.CreateDirectory(destination);

        SandboxWorkspace.SeedFrom(source, destination);

        File.Exists(Path.Combine(destination, "server.js")).Should().BeTrue();
        File.Exists(Path.Combine(destination, "nested", "data.json")).Should().BeTrue();
        File.ReadAllText(Path.Combine(destination, "server.js")).Should().Be("console.log('hi');");
    }

    [Fact]
    public void SeedFrom_SourceDeletedAfterSeeding_DestinationContentSurvives()
    {
        // The whole reason this is a copy contract, not a bind/link: the caller may delete the source
        // (e.g. a bundle's staging directory on handle eviction) on a lifecycle independent of the
        // sandbox session. Proves the copy has no residual dependency on the source once it returns.
        var source = Path.Combine(_root, "source");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "server.js"), "console.log('hi');");
        var destination = Path.Combine(_root, "dest");
        Directory.CreateDirectory(destination);

        SandboxWorkspace.SeedFrom(source, destination);
        Directory.Delete(source, recursive: true);

        File.Exists(Path.Combine(destination, "server.js")).Should().BeTrue();
    }

    [SkippableFact]
    public void SeedFrom_CopiedFilesAndDirectories_AreReadableByOtherUidsOnUnix()
    {
        // The bug an adversarial review caught that nothing else in this repo could: the container
        // reads its bind-mounted /workspace as a fixed unprivileged UID (65534) that never matches
        // the host process copying files into it. A copied file/dir left at umask-default permissions
        // is invisible to that UID — the container process cannot even traverse the directory, let
        // alone read the seeded server's own script. "Other" bits must be set explicitly, not left to
        // the host's ambient umask (which may be 0077 on a hardened host, silently breaking this on
        // exactly the deployments most likely to run it).
        Skip.If(OperatingSystem.IsWindows(), "Unix file-mode bits have no meaning on Windows.");

        var source = Path.Combine(_root, "source");
        Directory.CreateDirectory(Path.Combine(source, "nested"));
        File.WriteAllText(Path.Combine(source, "server.js"), "console.log('hi');");
        File.WriteAllText(Path.Combine(source, "nested", "data.json"), "{}");
        var destination = Path.Combine(_root, "dest");
        Directory.CreateDirectory(destination);

        SandboxWorkspace.SeedFrom(source, destination);

        var fileMode = File.GetUnixFileMode(Path.Combine(destination, "server.js"));
        fileMode.Should().HaveFlag(UnixFileMode.OtherRead,
            "a container process running as a different UID must be able to read the seeded file");

        var dirMode = File.GetUnixFileMode(Path.Combine(destination, "nested"));
        dirMode.Should().HaveFlag(UnixFileMode.OtherRead | UnixFileMode.OtherExecute,
            "a container process running as a different UID must be able to traverse into the seeded directory");
    }
}
