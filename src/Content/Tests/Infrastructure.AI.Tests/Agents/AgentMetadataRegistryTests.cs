using System.Collections.Concurrent;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Skills;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using FluentAssertions;
using Infrastructure.AI.Agents;
using Infrastructure.AI.Skills;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Tests.Common;
using Xunit;

namespace Infrastructure.AI.Tests.Agents;

/// <summary>
/// Tests for filesystem-based agent discovery via <see cref="AgentMetadataRegistry"/>.
/// Uses real AGENT.md files from the repo-root <c>agents/</c> directory where available;
/// when the directory is not present in the test-run environment the repo-facing tests
/// no-op to stay portable across CI and local runs.
/// </summary>
public sealed class AgentMetadataRegistryTests
{
    private static string RepoAgentsPath => Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "..", "agents"));

    private static AgentMetadataRegistry CreateRegistry(string? agentsPath = null)
        => CreateRegistry(new AgentOwnedSkillStore(), agentsPath);

    private static AgentMetadataRegistry CreateRegistry(AgentOwnedSkillStore ownedSkills, string? agentsPath = null)
        => CreateRegistry(ownedSkills, new UnsandboxedSkillFileReader(), agentsPath);

    private static AgentMetadataRegistry CreateRegistry(
        AgentOwnedSkillStore ownedSkills, ISkillFileReader skillFileReader, string? agentsPath = null)
    {
        var resolvedPath = agentsPath ?? RepoAgentsPath;
        var appConfig = new AppConfig
        {
            AI = new AIConfig
            {
                Agents = new AgentsConfig { BasePath = resolvedPath },
            },
        };
        return new AgentMetadataRegistry(
            NullLogger<AgentMetadataRegistry>.Instance,
            new OptionsMonitorStub(appConfig),
            new AgentMetadataParser(
                NullLogger<AgentMetadataParser>.Instance, TestMcpSecurityScanner.AlwaysSafe(), TestMcpSecurityScanner.DefaultConfig()),
            new SkillMetadataParser(
                NullLogger<SkillMetadataParser>.Instance, skillFileReader,
                TestMcpSecurityScanner.AlwaysSafe(), TestMcpSecurityScanner.DefaultConfig(),
                TestMcpSecurityScanner.RealEgressValidator()),
            skillFileReader,
            ownedSkills);
    }

    /// <summary>
    /// Delegates to a real <see cref="UnsandboxedSkillFileReader"/> for everything except
    /// <see cref="EnumerateDirectories"/> on a specific armed path, which throws — simulating the
    /// transient enumeration failure issue #705 identified
    /// (<see cref="NestedSkillScanner.Scan"/> reads through this interface, so it is the one seam
    /// that lets this scenario be tested deterministically and portably, unlike the un-interfaced
    /// <c>Directory.EnumerateDirectories</c> call in <c>AgentMetadataRegistry.DiscoverInDirectory</c>).
    /// </summary>
    private sealed class FailingEnumerateDirectoriesReader : ISkillFileReader
    {
        private readonly ISkillFileReader _inner = new UnsandboxedSkillFileReader();

        /// <summary>When set, <see cref="EnumerateDirectories"/> throws for exactly this path.</summary>
        public string? FailPath { get; set; }

        public string ReadText(string path) => _inner.ReadText(path);

        public Task<string> ReadTextAsync(string path, CancellationToken cancellationToken = default) =>
            _inner.ReadTextAsync(path, cancellationToken);

        public bool FileExists(string path) => _inner.FileExists(path);

        public bool DirectoryExists(string path) => _inner.DirectoryExists(path);

        public IReadOnlyList<string> EnumerateDirectories(string path) =>
            FailPath is not null && string.Equals(path, FailPath, StringComparison.OrdinalIgnoreCase)
                ? throw new IOException("Simulated transient enumeration failure")
                : _inner.EnumerateDirectories(path);
    }

    [Fact]
    public void Refresh_NestedSkillEnumerationFails_KeepsPreviouslyKnownOwnedSkillsRatherThanWipingThem()
    {
        // Closes the gap NestedSkillScanner's HadScanErrors flag exists for (issue #705): before this
        // fix, a transient failure enumerating an agent's own skills/ directory made the scan return an
        // empty list — indistinguishable from the agent genuinely owning no skills — and
        // SyncAgentOwnedSkills would then wipe the owned-skill store for it. AgentFactory falls back to
        // the GLOBAL skill registry for an unowned id, so this could silently swap in a different,
        // shared skill under the same id (see AgentMetadataRegistry.SyncAgentOwnedSkills' remarks).
        var tempRoot = Path.Combine(Path.GetTempPath(), $"agents-skill-scan-error-{Guid.NewGuid():N}");
        var store = new AgentOwnedSkillStore();
        var reader = new FailingEnumerateDirectoriesReader();
        try
        {
            WriteAgent(tempRoot, "alpha", """
                ---
                id: alpha
                name: Alpha
                skills: [alpha-only]
                ---
                """);
            WriteNestedSkill(tempRoot, "alpha", "alpha-only", "Alpha's private skill.");

            var registry = CreateRegistry(store, reader, agentsPath: tempRoot);
            registry.GetAll().Should().ContainSingle();
            store.TryGet("alpha", "alpha-only").Should().NotBeNull();

            // Arm the fake to fail enumerating exactly alpha's skills/ directory on the next scan —
            // alpha's own AGENT.md is untouched, only its nested skill scan fails.
            reader.FailPath = Path.Combine(tempRoot, "alpha", "skills");

            var summary = registry.Refresh();

            summary.Removed.Should().BeEmpty();
            registry.TryGet("alpha").Should().NotBeNull();

            // The previously-known owned skill must survive a failed scan, not be wiped by it.
            store.TryGet("alpha", "alpha-only").Should().NotBeNull();
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void GetAll_WithValidAgentsPath_ReturnsDiscoveredAgents()
    {
        if (!Directory.Exists(RepoAgentsPath))
            return;

        var registry = CreateRegistry();

        var agents = registry.GetAll();

        agents.Should().NotBeEmpty("the repo-root agents/ directory seeds at least one AGENT.md");
    }

    [Fact]
    public void TryGet_DefaultAgent_ReturnsDefinition()
    {
        if (!Directory.Exists(RepoAgentsPath))
            return;

        var registry = CreateRegistry();

        var agent = registry.TryGet("default");

        agent.Should().NotBeNull();
        agent!.Id.Should().Be("default");
        agent.Description.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void TryGet_NonExistentAgent_ReturnsNull()
    {
        var registry = CreateRegistry(agentsPath: Path.GetTempPath() + "no-agents-here");

        var agent = registry.TryGet("does-not-exist");

        agent.Should().BeNull();
    }

    [Fact]
    public void GetAll_EmptyAgentsPath_ReturnsEmptyList()
    {
        var registry = CreateRegistry(agentsPath: Path.GetTempPath() + "no-agents-here");

        var agents = registry.GetAll();

        agents.Should().BeEmpty();
    }

    [Fact]
    public void GetAll_DiscoversMultipleAgentsInSeparateSubdirectories()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"agents-multi-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            WriteAgent(tempRoot, "alpha", """
                ---
                id: alpha
                name: Alpha
                category: cat-a
                tags: ["one"]
                ---
                """);
            WriteAgent(tempRoot, "beta", """
                ---
                id: beta
                name: Beta
                category: cat-b
                tags: ["two"]
                ---
                """);

            var registry = CreateRegistry(agentsPath: tempRoot);

            registry.GetAll().Should().HaveCount(2);
            registry.GetByCategory("cat-a").Select(a => a.Id).Should().ContainSingle(id => id == "alpha");
            registry.GetByTags(["two"]).Select(a => a.Id).Should().ContainSingle(id => id == "beta");
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void IAgentMetadataRegistry_IsRegisteredInDI()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IOptionsMonitor<AppConfig>>(new OptionsMonitorStub(new AppConfig()));
        services.AddSingleton(TestMcpSecurityScanner.AlwaysSafe());
        services.AddSingleton(TestMcpSecurityScanner.DefaultConfig());
        services.AddSingleton<AgentMetadataParser>();
        services.AddSingleton<Application.AI.Common.Interfaces.Skills.ISkillFileReader,
            Infrastructure.AI.Skills.SkillFileReader>();
        services.AddSingleton<FluentValidation.IValidator<Domain.AI.Skills.EgressManifest>,
            Application.AI.Common.Skills.EgressManifestValidator>();
        services.AddSingleton<SkillMetadataParser>();
        services.AddSingleton<AgentOwnedSkillStore>();
        services.AddSingleton<IAgentMetadataRegistry, AgentMetadataRegistry>();

        using var provider = services.BuildServiceProvider();

        var registry = provider.GetService<IAgentMetadataRegistry>();
        registry.Should().NotBeNull();
    }

    [Fact]
    public void GetAll_AgentWithNestedSkills_RegistersThemUnderThatAgentOnly()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"agents-nested-{Guid.NewGuid():N}");
        var store = new AgentOwnedSkillStore();
        try
        {
            WriteAgent(tempRoot, "alpha", """
                ---
                id: alpha
                name: Alpha
                skills: [alpha-only]
                ---
                """);
            WriteNestedSkill(tempRoot, "alpha", "alpha-only", "Alpha's private skill.");

            WriteAgent(tempRoot, "beta", """
                ---
                id: beta
                name: Beta
                ---
                """);

            var registry = CreateRegistry(store, agentsPath: tempRoot);
            registry.GetAll().Should().HaveCount(2); // triggers discovery

            // Resolvable only for its owning agent...
            store.TryGet("alpha", "alpha-only").Should().NotBeNull();
            // ...invisible to another agent...
            store.TryGet("beta", "alpha-only").Should().BeNull();
            // ...and it never enters the global registry surface (the store is separate by construction).
            store.GetForAgent("alpha").Select(s => s.Id).Should().ContainSingle(id => id == "alpha-only");
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void GetAll_AgentWithoutSkillsDirectory_RegistersNothing()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"agents-noskills-{Guid.NewGuid():N}");
        var store = new AgentOwnedSkillStore();
        try
        {
            WriteAgent(tempRoot, "solo", """
                ---
                id: solo
                name: Solo
                ---
                """);

            var registry = CreateRegistry(store, agentsPath: tempRoot);
            registry.GetAll().Should().ContainSingle();

            store.GetForAgent("solo").Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void GetAll_NestedSkillDirWithoutSkillMd_IsSkippedAndDoesNotAbortDiscovery()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"agents-emptyskill-{Guid.NewGuid():N}");
        var store = new AgentOwnedSkillStore();
        try
        {
            WriteAgent(tempRoot, "alpha", """
                ---
                id: alpha
                name: Alpha
                ---
                """);
            WriteNestedSkill(tempRoot, "alpha", "good", "A valid nested skill.");

            // A skills/ subdir with no SKILL.md must be skipped without aborting discovery of the valid one.
            Directory.CreateDirectory(Path.Combine(tempRoot, "alpha", "skills", "empty"));

            var registry = CreateRegistry(store, agentsPath: tempRoot);
            registry.GetAll().Should().ContainSingle();

            store.GetForAgent("alpha").Select(s => s.Id).Should().ContainSingle(id => id == "good");
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void GetAll_DuplicateAgentId_KeepsFirstAndDoesNotMergeOwnedSkills()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"agents-dup-{Guid.NewGuid():N}");
        var store = new AgentOwnedSkillStore();
        try
        {
            // Two agent directories declaring the same id, each with its own private nested skill.
            WriteAgent(tempRoot, "first", """
                ---
                id: dup
                name: First
                ---
                """);
            WriteNestedSkill(tempRoot, "first", "first-skill", "First's private skill.");

            WriteAgent(tempRoot, "second", """
                ---
                id: dup
                name: Second
                ---
                """);
            WriteNestedSkill(tempRoot, "second", "second-skill", "Second's private skill.");

            var registry = CreateRegistry(store, agentsPath: tempRoot);
            registry.GetAll().Should().ContainSingle(a => a.Id == "dup");

            // Only the winning (first-discovered) agent's owned skills are registered — the colliding
            // agent's private skills must not merge into the same id's namespace.
            var owned = store.GetForAgent("dup").Select(s => s.Id).ToList();
            owned.Should().ContainSingle();
            owned.Should().BeSubsetOf(["first-skill", "second-skill"]);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Refresh_NewAgentAddedToDisk_ReportsItAsAdded()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"agents-refresh-add-{Guid.NewGuid():N}");
        try
        {
            WriteAgent(tempRoot, "alpha", """
                ---
                id: alpha
                name: Alpha
                ---
                """);

            var registry = CreateRegistry(agentsPath: tempRoot);
            registry.GetAll().Should().ContainSingle(); // loads the initial cache

            WriteAgent(tempRoot, "beta", """
                ---
                id: beta
                name: Beta
                ---
                """);

            var summary = registry.Refresh();

            summary.Added.Should().ContainSingle(id => id == "beta");
            summary.Updated.Should().BeEmpty();
            summary.Removed.Should().BeEmpty();
            summary.TotalAgentCount.Should().Be(2);
            registry.GetAll().Should().HaveCount(2);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Refresh_AgentManifestEdited_ReportsItAsUpdated()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"agents-refresh-edit-{Guid.NewGuid():N}");
        try
        {
            WriteAgent(tempRoot, "alpha", """
                ---
                id: alpha
                name: Alpha
                ---
                """);

            var registry = CreateRegistry(agentsPath: tempRoot);
            registry.GetAll().Should().ContainSingle();

            WriteAgent(tempRoot, "alpha", """
                ---
                id: alpha
                name: Alpha Renamed
                ---
                """);

            var summary = registry.Refresh();

            summary.Updated.Should().ContainSingle(id => id == "alpha");
            summary.Added.Should().BeEmpty();
            summary.Removed.Should().BeEmpty();
            registry.TryGet("alpha")!.Name.Should().Be("Alpha Renamed");
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Refresh_ManifestUnchangedOnDisk_IsNotReportedAsUpdated()
    {
        // Regression guard: AgentDefinition.LoadedAt is stamped fresh on every parse, so a naive
        // record-equality diff would classify every still-present, byte-for-byte-unchanged agent as
        // "updated" on every single refresh.
        var tempRoot = Path.Combine(Path.GetTempPath(), $"agents-refresh-noop-{Guid.NewGuid():N}");
        try
        {
            WriteAgent(tempRoot, "alpha", """
                ---
                id: alpha
                name: Alpha
                ---
                """);

            var registry = CreateRegistry(agentsPath: tempRoot);
            registry.GetAll().Should().ContainSingle();

            var summary = registry.Refresh();

            summary.Updated.Should().BeEmpty();
            summary.Added.Should().BeEmpty();
            summary.Removed.Should().BeEmpty();
            summary.TotalAgentCount.Should().Be(1);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Refresh_AgentDirectoryDeleted_RemovesAgentAndItsOwnedSkills()
    {
        // The orphan regression this feature exists to close: AgentOwnedSkillStore was add-only, so
        // a naive reload would drop the agent from the registry but leave its nested skills resolvable
        // by an id no agent owns any more.
        var tempRoot = Path.Combine(Path.GetTempPath(), $"agents-refresh-remove-{Guid.NewGuid():N}");
        var store = new AgentOwnedSkillStore();
        try
        {
            WriteAgent(tempRoot, "alpha", """
                ---
                id: alpha
                name: Alpha
                skills: [alpha-only]
                ---
                """);
            WriteNestedSkill(tempRoot, "alpha", "alpha-only", "Alpha's private skill.");

            var registry = CreateRegistry(store, agentsPath: tempRoot);
            registry.GetAll().Should().ContainSingle();
            store.TryGet("alpha", "alpha-only").Should().NotBeNull();

            Directory.Delete(Path.Combine(tempRoot, "alpha"), recursive: true);

            var summary = registry.Refresh();

            summary.Removed.Should().ContainSingle(id => id == "alpha");
            summary.TotalAgentCount.Should().Be(0);
            registry.GetAll().Should().BeEmpty();
            registry.TryGet("alpha").Should().BeNull();

            // The orphan check: the agent's nested skill must not linger in the owned-skill store.
            store.GetForAgent("alpha").Should().BeEmpty();
            store.TryGet("alpha", "alpha-only").Should().BeNull();
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Refresh_AgentKeepsExistingButDeletesOneNestedSkill_DropsOnlyThatSkill()
    {
        // A narrower version of the same add-only defect: the AGENT still exists, but one of its
        // nested SKILL.md files was removed. ReplaceAgentSkills (a full swap) must drop it; the old
        // per-skill Register call could never observe a deletion.
        var tempRoot = Path.Combine(Path.GetTempPath(), $"agents-refresh-skill-remove-{Guid.NewGuid():N}");
        var store = new AgentOwnedSkillStore();
        try
        {
            WriteAgent(tempRoot, "alpha", """
                ---
                id: alpha
                name: Alpha
                skills: [keep, drop]
                ---
                """);
            WriteNestedSkill(tempRoot, "alpha", "keep", "Kept skill.");
            WriteNestedSkill(tempRoot, "alpha", "drop", "Dropped skill.");

            var registry = CreateRegistry(store, agentsPath: tempRoot);
            registry.GetAll().Should().ContainSingle();
            store.GetForAgent("alpha").Select(s => s.Id).Should().BeEquivalentTo(["keep", "drop"]);

            Directory.Delete(Path.Combine(tempRoot, "alpha", "skills", "drop"), recursive: true);

            registry.Refresh();

            store.GetForAgent("alpha").Select(s => s.Id).Should().BeEquivalentTo(["keep"]);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Invalidate_AgentDirectoryDeleted_ThenNextRead_RemovesAgentAndItsOwnedSkills()
    {
        // The bug this test exists to catch (caught by CI's correctness/grader gates on the first
        // pass): the automatic watcher path calls ONLY Invalidate, never Refresh. If reconciliation
        // lived exclusively in Refresh (as the first cut of this feature had it), a deleted agent's
        // owned skills would never be cleaned up on the default, no-restart path this issue exists to
        // support — only an operator explicitly hitting the refresh endpoint would clean them up. This
        // drives the same scenario as Refresh_AgentDirectoryDeleted_RemovesAgentAndItsOwnedSkills but
        // through Invalidate + a lazy read instead of an explicit Refresh call.
        var tempRoot = Path.Combine(Path.GetTempPath(), $"agents-invalidate-remove-{Guid.NewGuid():N}");
        var store = new AgentOwnedSkillStore();
        try
        {
            WriteAgent(tempRoot, "alpha", """
                ---
                id: alpha
                name: Alpha
                skills: [alpha-only]
                ---
                """);
            WriteNestedSkill(tempRoot, "alpha", "alpha-only", "Alpha's private skill.");

            var registry = CreateRegistry(store, agentsPath: tempRoot);
            registry.GetAll().Should().ContainSingle();
            store.TryGet("alpha", "alpha-only").Should().NotBeNull();

            Directory.Delete(Path.Combine(tempRoot, "alpha"), recursive: true);

            registry.Invalidate();
            registry.GetAll().Should().BeEmpty();
            registry.TryGet("alpha").Should().BeNull();

            // The orphan check, via the automatic (Invalidate, not Refresh) path.
            store.GetForAgent("alpha").Should().BeEmpty();
            store.TryGet("alpha", "alpha-only").Should().BeNull();
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Refresh_ConfiguredRootVanishes_KeepsPreviouslyKnownAgentsRatherThanWipingTheRegistry()
    {
        // CI correctness-review finding on #705: AgentSearchPathResolver.Resolve treats "this root
        // doesn't exist" as an ORDINARY, expected condition — an unused AdditionalPaths entry must
        // not break discovery of everything else — so it never sets hadEnumerationErrors. A
        // previously-resolved root that transiently stops resolving (a network mount blip, a
        // disk-readiness race at boot) looks identical to "the whole tree was deleted" from
        // Discover()'s point of view, and without this guard would wipe the ENTIRE registry and
        // every owned skill, with nothing to self-heal from — worse than an active enumeration
        // exception, not better.
        var tempRoot = Path.Combine(Path.GetTempPath(), $"agents-root-vanishes-{Guid.NewGuid():N}");
        var store = new AgentOwnedSkillStore();
        try
        {
            WriteAgent(tempRoot, "alpha", """
                ---
                id: alpha
                name: Alpha
                skills: [alpha-only]
                ---
                """);
            WriteNestedSkill(tempRoot, "alpha", "alpha-only", "Alpha's private skill.");

            var registry = CreateRegistry(store, agentsPath: tempRoot);
            registry.GetAll().Should().ContainSingle();
            store.TryGet("alpha", "alpha-only").Should().NotBeNull();

            // The root vanishes entirely — logged as a normal "not found, skipping", never as an
            // enumeration error.
            Directory.Delete(tempRoot, recursive: true);

            var summary = registry.Refresh();

            summary.Removed.Should().BeEmpty();
            registry.GetAll().Should().ContainSingle(a => a.Id == "alpha");
            store.TryGet("alpha", "alpha-only").Should().NotBeNull();
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Refresh_AfterWatcherStyleInvalidate_DiffsAgainstLastKnownStateNotAnEmptySet()
    {
        // The correctness-gate finding on the first pass: Refresh used to diff against `_cache`
        // directly, and Invalidate used to null that field. A watcher-triggered Invalidate landing
        // before an operator's Refresh meant Refresh's "previous" baseline was empty — every surviving
        // agent misreported as newly "added," and any real removal was missed entirely (including its
        // owned-skill cleanup, since that only runs for ids Refresh classifies as removed).
        var tempRoot = Path.Combine(Path.GetTempPath(), $"agents-refresh-after-invalidate-{Guid.NewGuid():N}");
        try
        {
            WriteAgent(tempRoot, "alpha", """
                ---
                id: alpha
                name: Alpha
                ---
                """);
            WriteAgent(tempRoot, "beta", """
                ---
                id: beta
                name: Beta
                ---
                """);

            var registry = CreateRegistry(agentsPath: tempRoot);
            registry.GetAll().Should().HaveCount(2);

            // Simulates the watcher noticing an unrelated change and invalidating first.
            registry.Invalidate();

            Directory.Delete(Path.Combine(tempRoot, "beta"), recursive: true);

            var summary = registry.Refresh();

            summary.Added.Should().BeEmpty();
            summary.Removed.Should().ContainSingle(id => id == "beta");
            summary.TotalAgentCount.Should().Be(1);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Invalidate_ThenNextRead_RescansFilesystem()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"agents-invalidate-{Guid.NewGuid():N}");
        try
        {
            WriteAgent(tempRoot, "alpha", """
                ---
                id: alpha
                name: Alpha
                ---
                """);

            var registry = CreateRegistry(agentsPath: tempRoot);
            registry.GetAll().Should().ContainSingle();

            WriteAgent(tempRoot, "beta", """
                ---
                id: beta
                name: Beta
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
    public async Task ConcurrentReadsDuringRepeatedInvalidation_NeverThrowAndAlwaysSeeACompleteSet()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"agents-concurrency-{Guid.NewGuid():N}");
        try
        {
            for (var i = 0; i < 5; i++)
            {
                WriteAgent(tempRoot, $"agent-{i}", $"""
                    ---
                    id: agent-{i}
                    name: Agent {i}
                    ---
                    """);
            }

            var registry = CreateRegistry(agentsPath: tempRoot);
            registry.GetAll().Should().HaveCount(5);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var readerExceptions = new ConcurrentBag<Exception>();

            var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
            {
                try
                {
                    while (!cts.IsCancellationRequested)
                    {
                        // A reader mid-reload must never see a partially-built dictionary — every
                        // snapshot returned by GetAll is either the old complete set or the new one.
                        registry.GetAll().Should().HaveCount(5);
                    }
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

    private static void WriteAgent(string root, string folderName, string content)
    {
        var dir = Path.Combine(root, folderName);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "AGENT.md"), content);
    }

    private static void WriteNestedSkill(string root, string agentFolder, string skillId, string body)
    {
        var dir = Path.Combine(root, agentFolder, "skills", skillId);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), $"""
            ---
            id: {skillId}
            name: {skillId}
            ---
            {body}
            """);
    }

    private sealed class OptionsMonitorStub : IOptionsMonitor<AppConfig>
    {
        public OptionsMonitorStub(AppConfig value) => CurrentValue = value;
        public AppConfig CurrentValue { get; }
        public AppConfig Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<AppConfig, string?> listener) => null;
    }
}
