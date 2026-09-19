using System.Collections.Concurrent;
using Application.AI.Common.Exceptions;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Skills;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using FluentAssertions;
using Infrastructure.AI.Skills;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Tests.Common;
using Xunit;

namespace Infrastructure.AI.Tests.Skills;

/// <summary>
/// Tests for filesystem-based skill discovery via <see cref="SkillMetadataRegistry"/>.
/// Uses real SKILL.md files from the top-level skills/ directory for read-path coverage, and
/// temp-directory-based fixtures (mirroring <c>AgentMetadataRegistryTests</c> from issue #705) for
/// reload/reconciliation coverage.
/// </summary>
public sealed class SkillMetadataRegistryTests
{
    private static string SkillsPath => Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "..", "skills"));

    private static SkillMetadataRegistry CreateRegistry(string? skillsPath = null)
        => CreateRegistry(new UnsandboxedSkillFileReader(), skillsPath);

    private static SkillMetadataRegistry CreateRegistry(ISkillFileReader fileReader, string? skillsPath = null)
    {
        var resolvedPath = skillsPath ?? SkillsPath;
        var appConfig = new AppConfig
        {
            AI = new AIConfig
            {
                Skills = new SkillsConfig { BasePath = resolvedPath }
            }
        };
        var optionsMonitor = new OptionsMonitorStub(appConfig);
        var logger = NullLogger<SkillMetadataRegistry>.Instance;
        var parser = new SkillMetadataParser(
            NullLogger<SkillMetadataParser>.Instance, fileReader,
            TestMcpSecurityScanner.AlwaysSafe(), TestMcpSecurityScanner.DefaultConfig(),
            TestMcpSecurityScanner.RealEgressValidator());

        return new SkillMetadataRegistry(logger, optionsMonitor, parser, fileReader);
    }

    private static void WriteSkill(string root, string folderName, string content)
    {
        var dir = Path.Combine(root, folderName);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), content);
    }

    /// <summary>
    /// Delegates to a real <see cref="UnsandboxedSkillFileReader"/> for everything except
    /// <see cref="EnumerateDirectories"/> on a specific armed path, which throws — simulating the
    /// transient enumeration failure the enumeration-error guard exists to tolerate. Mirrors
    /// <c>AgentMetadataRegistryTests.FailingEnumerateDirectoriesReader</c> from issue #705.
    /// </summary>
    private sealed class FailingEnumerateDirectoriesReader : ISkillFileReader
    {
        private readonly ISkillFileReader _inner = new UnsandboxedSkillFileReader();

        /// <summary>When set, <see cref="EnumerateDirectories"/> throws for exactly this path.</summary>
        public string? FailPath { get; set; }

        /// <summary>When set, <see cref="EnumerateDirectories"/> throws a <see cref="SkillPathRefusedException"/> for exactly this path.</summary>
        public string? RefusePath { get; set; }

        public string ReadText(string path) => _inner.ReadText(path);

        public Task<string> ReadTextAsync(string path, CancellationToken cancellationToken = default) =>
            _inner.ReadTextAsync(path, cancellationToken);

        public bool FileExists(string path) => _inner.FileExists(path);

        public bool DirectoryExists(string path) => _inner.DirectoryExists(path);

        public IReadOnlyList<string> EnumerateDirectories(string path)
        {
            if (RefusePath is not null && string.Equals(path, RefusePath, StringComparison.OrdinalIgnoreCase))
                throw new SkillPathRefusedException($"Path refused: {path}");
            if (FailPath is not null && string.Equals(path, FailPath, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Simulated transient enumeration failure");
            return _inner.EnumerateDirectories(path);
        }
    }

    [Fact]
    public void GetAll_PluginsDeclaredButNoPluginRegistry_ThrowsFailFast()
    {
        // Fail-fast guard: declaring plugins without an IPluginRegistry would silently no-op plugin
        // boundary governance (attribution never happens). A clear startup exception is required.
        var appConfig = new AppConfig
        {
            AI = new AIConfig
            {
                Skills = new SkillsConfig { BasePath = SkillsPath },
                Plugins = new Domain.Common.Config.AI.Plugins.PluginsConfig
                {
                    Packages =
                    [
                        new Domain.Common.Config.AI.Plugins.PluginDeclaration { Name = "some-plugin", Path = "./plugins/some-plugin" }
                    ]
                }
            }
        };
        var registry = new SkillMetadataRegistry(
            NullLogger<SkillMetadataRegistry>.Instance,
            new OptionsMonitorStub(appConfig),
            new SkillMetadataParser(
            NullLogger<SkillMetadataParser>.Instance, new UnsandboxedSkillFileReader(),
            TestMcpSecurityScanner.AlwaysSafe(), TestMcpSecurityScanner.DefaultConfig(),
            TestMcpSecurityScanner.RealEgressValidator()),
            new UnsandboxedSkillFileReader(),
            pluginRegistry: null);

        var act = () => registry.GetAll();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*IPluginRegistry*");
    }

    [Fact]
    public void GetAll_WithValidSkillsPath_ReturnsDiscoveredSkills()
    {
        if (!Directory.Exists(SkillsPath))
            return; // Skills dir not present in this test run environment — skip

        var registry = CreateRegistry();

        var skills = registry.GetAll();

        skills.Should().NotBeEmpty("skills/ directory has SKILL.md files");
    }

    [Fact]
    public void TryGet_ResearchAgent_ReturnsDefinition()
    {
        if (!Directory.Exists(SkillsPath))
            return;

        var registry = CreateRegistry();

        var skill = registry.TryGet("research-agent");

        skill.Should().NotBeNull();
        skill!.Id.Should().Be("research-agent");
        skill.Description.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void TryGet_OrchestratorAgent_ReturnsDefinition()
    {
        if (!Directory.Exists(SkillsPath))
            return;

        var registry = CreateRegistry();

        var skill = registry.TryGet("orchestrator-agent");

        skill.Should().NotBeNull();
        skill!.Id.Should().Be("orchestrator-agent");
        skill.Category.Should().Be("orchestration");
    }

    [Fact]
    public void TryGet_NonExistentSkill_ReturnsNull()
    {
        if (!Directory.Exists(SkillsPath))
            return;

        var registry = CreateRegistry();

        var skill = registry.TryGet("does-not-exist");

        skill.Should().BeNull();
    }

    [Fact]
    public void GetByCategory_Research_ReturnsResearchSkills()
    {
        if (!Directory.Exists(SkillsPath))
            return;

        var registry = CreateRegistry();

        var skills = registry.GetByCategory("research");

        skills.Should().Contain(s => s.Id == "research-agent");
    }

    [Fact]
    public void GetByTags_Orchestrator_ReturnsOrchestratorSkill()
    {
        if (!Directory.Exists(SkillsPath))
            return;

        var registry = CreateRegistry();

        var skills = registry.GetByTags(["orchestrator"]);

        skills.Should().Contain(s => s.Id == "orchestrator-agent");
    }

    [Fact]
    public void GetAll_EmptySkillsPath_ReturnsEmptyList()
    {
        var registry = CreateRegistry(skillsPath: Path.GetTempPath() + "no-skills-here");

        var skills = registry.GetAll();

        skills.Should().BeEmpty();
    }

    [Fact]
    public void ISkillMetadataRegistry_IsRegisteredInDI()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IOptionsMonitor<AppConfig>>(new OptionsMonitorStub(new AppConfig()));
        services.AddSingleton(TestMcpSecurityScanner.AlwaysSafe());
        services.AddSingleton(TestMcpSecurityScanner.DefaultConfig());
        services.AddSingleton<Application.AI.Common.Interfaces.Skills.ISkillFileReader,
            Infrastructure.AI.Skills.SkillFileReader>();
        services.AddSingleton<FluentValidation.IValidator<Domain.AI.Skills.EgressManifest>,
            Application.AI.Common.Skills.EgressManifestValidator>();
        services.AddSingleton<SkillMetadataParser>();
        services.AddSingleton<ISkillMetadataRegistry, SkillMetadataRegistry>();

        using var provider = services.BuildServiceProvider();

        var registry = provider.GetService<ISkillMetadataRegistry>();
        registry.Should().NotBeNull();
    }

    [Fact]
    public void SkillMetadataRegistry_IncludesObjectives_InReturnedSkillDefinition()
    {
        if (!Directory.Exists(SkillsPath))
            return;

        var registry = CreateRegistry();

        var skill = registry.TryGet("harness-proposer");

        skill.Should().NotBeNull("harness-proposer SKILL.md includes ## Objectives");
        skill!.Objectives.Should().NotBeNullOrWhiteSpace();
        skill.HasObjectives.Should().BeTrue();
    }

    [Fact]
    public void SkillMetadataRegistry_ExistingSkillsWithoutNewSections_ParseCorrectly()
    {
        if (!Directory.Exists(SkillsPath))
            return;

        var registry = CreateRegistry();

        // orchestrator-agent has no ## Objectives or ## Trace Format — should parse without error
        var skill = registry.TryGet("orchestrator-agent");

        skill.Should().NotBeNull();
        skill!.Objectives.Should().BeNull();
        skill.TraceFormat.Should().BeNull();
        skill.Instructions.Should().NotBeNullOrWhiteSpace();
    }

    // --- Reload / reconciliation coverage (issue #709) ---

    [Fact]
    public void Refresh_DiscoversMultipleSkillsInSeparateSubdirectories()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"skills-multi-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            WriteSkill(tempRoot, "alpha", """
                ---
                name: alpha
                category: cat-a
                tags: ["one"]
                ---
                """);
            WriteSkill(tempRoot, "beta", """
                ---
                name: beta
                category: cat-b
                tags: ["two"]
                ---
                """);

            var registry = CreateRegistry(skillsPath: tempRoot);

            registry.GetAll().Should().HaveCount(2);
            registry.GetByCategory("cat-a").Select(s => s.Id).Should().ContainSingle(id => id == "alpha");
            registry.GetByTags(["two"]).Select(s => s.Id).Should().ContainSingle(id => id == "beta");
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Refresh_AddUpdateRemove_ReportsAccurateSummary()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"skills-refresh-summary-{Guid.NewGuid():N}");
        try
        {
            WriteSkill(tempRoot, "alpha", """
                ---
                name: alpha
                ---
                """);
            WriteSkill(tempRoot, "beta", """
                ---
                name: beta
                ---
                """);

            var registry = CreateRegistry(skillsPath: tempRoot);
            registry.GetAll().Should().HaveCount(2);

            // alpha updated (description changed, id/name unchanged — Id is derived from Name, so
            // renaming would change identity rather than update the same skill), beta removed,
            // gamma added.
            WriteSkill(tempRoot, "alpha", """
                ---
                name: alpha
                description: Now with a description.
                ---
                """);
            Directory.Delete(Path.Combine(tempRoot, "beta"), recursive: true);
            WriteSkill(tempRoot, "gamma", """
                ---
                name: gamma
                ---
                """);

            var summary = registry.Refresh();

            summary.Added.Should().ContainSingle(id => id == "gamma");
            summary.Updated.Should().ContainSingle(id => id == "alpha");
            summary.Removed.Should().ContainSingle(id => id == "beta");
            summary.TotalSkillCount.Should().Be(2);
            registry.TryGet("beta").Should().BeNull();
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Invalidate_ThenNextRead_RescansFilesystem()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"skills-invalidate-{Guid.NewGuid():N}");
        try
        {
            WriteSkill(tempRoot, "alpha", """
                ---
                name: alpha
                ---
                """);

            var registry = CreateRegistry(skillsPath: tempRoot);
            registry.GetAll().Should().ContainSingle();

            WriteSkill(tempRoot, "beta", """
                ---
                name: beta
                ---
                """);

            // Without Invalidate, the cache would still hold only "alpha" — GetAll never rescans on
            // its own.
            registry.Invalidate();

            registry.GetAll().Should().HaveCount(2);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Refresh_DirectoryEnumerationFails_KeepsPreviouslyKnownSkillRatherThanDroppingIt()
    {
        // Closes the gap the enumeration-error guard exists for: a transient failure enumerating a
        // sibling directory must not be indistinguishable from "this skill was really deleted."
        var tempRoot = Path.Combine(Path.GetTempPath(), $"skills-enum-error-{Guid.NewGuid():N}");
        var reader = new FailingEnumerateDirectoriesReader();
        try
        {
            WriteSkill(Path.Combine(tempRoot, "group"), "alpha", """
                ---
                name: alpha
                ---
                """);

            var registry = CreateRegistry(reader, tempRoot);
            registry.GetAll().Should().ContainSingle();

            // "group" still exists as a real directory — it's still returned by the (unmediated)
            // enumeration of tempRoot — but enumerating ITS contents now throws, simulating a
            // transient failure (permission hiccup, network stutter) reading exactly the directory
            // "alpha" lives under, without needing to touch the filesystem at all.
            reader.FailPath = Path.Combine(tempRoot, "group");

            var summary = registry.Refresh();

            summary.Removed.Should().BeEmpty();
            registry.TryGet("alpha").Should().NotBeNull();
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Refresh_ConfiguredRootVanishes_KeepsPreviouslyKnownSkillsRatherThanWipingTheRegistry()
    {
        // Mirrors AgentMetadataRegistryTests' equivalent (#705): SkillSearchPathResolver.Resolve
        // treats "this root doesn't exist" as an ORDINARY, expected condition, so it never sets
        // hadEnumerationErrors. A previously-resolved root that transiently stops resolving must not
        // wipe the entire registry.
        var tempRoot = Path.Combine(Path.GetTempPath(), $"skills-root-vanishes-{Guid.NewGuid():N}");
        try
        {
            WriteSkill(tempRoot, "alpha", """
                ---
                name: alpha
                ---
                """);

            var registry = CreateRegistry(skillsPath: tempRoot);
            registry.GetAll().Should().ContainSingle();

            Directory.Delete(tempRoot, recursive: true);

            var summary = registry.Refresh();

            summary.Removed.Should().BeEmpty();
            registry.GetAll().Should().ContainSingle(s => s.Id == "alpha");
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void GetAll_SandboxRefusal_PropagatesRatherThanBeingToleratedAsStale()
    {
        // A sandbox refusal is a security-relevant misconfiguration, not an ordinary transient
        // failure — GetOrLoadCache's failure-tolerance catch must NOT absorb it, even when a
        // previously-good cache exists to "fall back" to.
        var tempRoot = Path.Combine(Path.GetTempPath(), $"skills-sandbox-refusal-{Guid.NewGuid():N}");
        var reader = new FailingEnumerateDirectoriesReader();
        try
        {
            WriteSkill(Path.Combine(tempRoot, "group"), "alpha", """
                ---
                name: alpha
                ---
                """);

            var registry = CreateRegistry(reader, tempRoot);
            registry.GetAll().Should().ContainSingle();

            reader.RefusePath = Path.Combine(tempRoot, "group");
            registry.Invalidate();

            var act = () => registry.GetAll();

            act.Should().Throw<SkillPathRefusedException>();
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ConcurrentReadsDuringRepeatedInvalidation_NeverThrowAndAlwaysSeeACompleteSet()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"skills-concurrency-{Guid.NewGuid():N}");
        try
        {
            for (var i = 0; i < 5; i++)
            {
                WriteSkill(tempRoot, $"skill-{i}", $"""
                    ---
                    name: skill-{i}
                    ---
                    """);
            }

            var registry = CreateRegistry(skillsPath: tempRoot);
            registry.GetAll().Should().HaveCount(5);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var readerExceptions = new ConcurrentBag<Exception>();

            var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
            {
                try
                {
                    while (!cts.IsCancellationRequested)
                        registry.GetAll().Should().HaveCount(5);
                }
                catch (Exception ex)
                {
                    readerExceptions.Add(ex);
                }
            }));

            var invalidator = Task.Run(() =>
            {
                while (!cts.IsCancellationRequested)
                    registry.Invalidate();
            });

            await Task.WhenAll([.. readers, invalidator]);

            readerExceptions.Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private sealed class OptionsMonitorStub : IOptionsMonitor<AppConfig>
    {
        public OptionsMonitorStub(AppConfig value) => CurrentValue = value;
        public AppConfig CurrentValue { get; }
        public AppConfig Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<AppConfig, string?> listener) => null;
    }
}
