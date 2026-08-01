using Application.Core.CQRS.Evaluation.RunEvalSuite;
using Domain.Common.Config;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.Evaluation.Tests.CQRS;

/// <summary>
/// Tests for <see cref="EvalDatasetPathGuard"/> — which dataset files a run may read.
/// </summary>
/// <remarks>
/// These are the tests that matter for exposing evaluation over HTTP. Without confinement,
/// <c>RunEvalSuiteCommand</c> accepts any path the process can read and reports whether it exists,
/// which is an arbitrary-file-read probe and a filesystem oracle in one. Each case below is a way a
/// caller would try to get outside the allowed roots.
/// </remarks>
public sealed class EvalDatasetPathGuardTests : IDisposable
{
    private readonly string _root;
    private readonly string _outside;

    public EvalDatasetPathGuardTests()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "eval-guard-" + Guid.NewGuid().ToString("N"));
        _root = Path.Combine(baseDir, "allowed");
        _outside = Path.Combine(baseDir, "secret");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_outside);
    }

    public void Dispose()
    {
        var parent = Path.GetDirectoryName(_root);
        if (parent is not null && Directory.Exists(parent))
            Directory.Delete(parent, recursive: true);
    }

    private static EvalDatasetPathGuard GuardWith(params string[] roots)
    {
        var config = new AppConfig();
        config.AI.Evaluation.DatasetRoots = [.. roots];
        return GuardOver(config, startedConfined: false);
    }

    /// <summary>
    /// Builds a guard over live <paramref name="config"/> so a test can mutate it afterwards and observe
    /// what the guard does with the change.
    /// </summary>
    /// <param name="startedConfined">
    /// What the composition root recorded at boot. Passed in rather than derived, exactly as in
    /// production — the guard is a lazy singleton, so a latch it computed for itself would record
    /// whatever configuration said at first dispatch instead of at startup.
    /// </param>
    private static EvalDatasetPathGuard GuardOver(AppConfig config, bool startedConfined)
    {
        var monitor = new Mock<IOptionsMonitor<AppConfig>>();
        monitor.SetupGet(m => m.CurrentValue).Returns(config);
        return new EvalDatasetPathGuard(monitor.Object, new EvalConfinementLatch(startedConfined));
    }

    private string WriteFile(string directory, string name)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, "cases: []");
        return path;
    }

    [Fact]
    public void NoRootsConfigured_AllowsAnyExistingFile()
    {
        // The shipped default. The EvalRunner CLI's whole workflow is pointing at a file anywhere on
        // disk, and a local developer can read those files regardless — making roots mandatory would
        // break the tool for no security gain.
        var outsideFile = WriteFile(_outside, "local.yaml");

        var decision = GuardWith().Resolve(outsideFile);

        decision.IsAllowed.Should().BeTrue();
        decision.CanonicalPath.Should().Be(Path.GetFullPath(outsideFile));
    }

    [Fact]
    public void NoRootsConfigured_StillRefusesAMissingFile()
    {
        var decision = GuardWith().Resolve(Path.Combine(_outside, "absent.yaml"));

        decision.IsAllowed.Should().BeFalse();
    }

    [Fact]
    public void RootConfigured_AllowsAFileInsideIt()
    {
        var inside = WriteFile(_root, "suite.yaml");

        var decision = GuardWith(_root).Resolve(inside);

        decision.IsAllowed.Should().BeTrue();
        decision.CanonicalPath.Should().Be(Path.GetFullPath(inside));
    }

    [Fact]
    public void RootConfigured_RefusesAFileOutsideIt()
    {
        var outsideFile = WriteFile(_outside, "secrets.yaml");

        GuardWith(_root).Resolve(outsideFile).IsAllowed.Should().BeFalse();
    }

    [Fact]
    public void RootConfigured_RefusesTraversalOutOfTheRoot()
    {
        // The attack the canonicalisation exists for: this string starts with the allowed root and
        // resolves outside it. A guard comparing raw strings would admit it.
        WriteFile(_outside, "secrets.yaml");
        var traversal = Path.Combine(_root, "..", "secret", "secrets.yaml");

        var decision = GuardWith(_root).Resolve(traversal);

        decision.IsAllowed.Should().BeFalse();
    }

    [Fact]
    public void RootConfigured_RefusesASiblingRootSharingThePrefix()
    {
        // "/…/allowed-elsewhere" starts with "/…/allowed" as a string but is a different directory.
        // Comparing without a trailing separator would admit every sibling whose name extends the root.
        var sibling = _root + "-elsewhere";
        Directory.CreateDirectory(sibling);
        var file = WriteFile(sibling, "suite.yaml");

        GuardWith(_root).Resolve(file).IsAllowed.Should().BeFalse();
    }

    [Fact]
    public void RootConfigured_RefusalsDoNotDistinguishAbsentFromForbidden()
    {
        // Distinguishing them turns the endpoint into a filesystem oracle: a caller could map the disk
        // by watching which message came back for each probe.
        var forbidden = WriteFile(_outside, "exists-but-forbidden.yaml");
        var absent = Path.Combine(_root, "does-not-exist.yaml");

        var guard = GuardWith(_root);

        guard.Resolve(forbidden).Reason.Should().Be(guard.Resolve(absent).Reason);
    }

    [Fact]
    public void RootConfigured_RefusesASymlinkInsideTheRootPointingOutside()
    {
        // A link that lives inside the root but resolves outside it. The file the loader ultimately
        // opens is the target, so the target is what must be checked.
        var target = WriteFile(_outside, "secrets.yaml");
        var link = Path.Combine(_root, "innocent.yaml");

        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Windows needs Developer Mode or elevation to create symlinks. Skipping is honest here;
            // the assertion below would otherwise pass for the wrong reason (link never created).
            return;
        }

        GuardWith(_root).Resolve(link).IsAllowed.Should().BeFalse();
    }

    [Fact]
    public void RootConfigured_RefusesAFileBeneathADirectoryLinkPointingOutside()
    {
        // The evasion that resolving only the final path segment leaves open. Nothing about
        // "…/allowed/window/secrets.yaml" looks suspicious — it canonicalises to a path starting with
        // the allowed root — but "window" is a link and the file opened lives outside. Every segment
        // has to be resolved, not just the leaf.
        var target = WriteFile(_outside, "secrets.yaml");
        var linkedDirectory = Path.Combine(_root, "window");

        try
        {
            Directory.CreateSymbolicLink(linkedDirectory, _outside);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Windows needs Developer Mode or elevation. Skipping is honest; the assertion would
            // otherwise pass because the link was never created.
            return;
        }

        var throughTheLink = Path.Combine(linkedDirectory, Path.GetFileName(target));
        File.Exists(throughTheLink).Should().BeTrue("the link must actually reach the file, or this proves nothing");

        GuardWith(_root).Resolve(throughTheLink).IsAllowed.Should().BeFalse();
    }

    [Fact]
    public void RootConfigured_RefusesAChainLongerThanTheHopBudget()
    {
        // Running out of budget means the walk did not finish, so the path in hand may still be a link.
        // Here every link of the chain lives inside the allowed root while the real file does not — so
        // giving up and answering with whatever segment we stopped on would pass containment and hand
        // the loader an escape. Not knowing where a path leads has to refuse, like every other failure
        // mode in this guard.
        var target = WriteFile(_outside, "secrets.yaml");
        var chain = new List<string>();

        try
        {
            // Longer than MaxLinkHops (16). Built tail-first so each link points at the one after it.
            var next = target;
            for (var i = 0; i < 20; i++)
            {
                var link = Path.Combine(_root, $"hop-{i}.yaml");
                File.CreateSymbolicLink(link, next);
                chain.Add(link);
                next = link;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return; // Windows without Developer Mode — skipping beats passing for the wrong reason.
        }

        var head = chain[^1];
        File.Exists(head).Should().BeTrue("the OS must resolve the whole chain, or this proves nothing");

        GuardWith(_root).Resolve(head).IsAllowed.Should().BeFalse();
    }

    [Fact]
    public void RootReachedThroughASymlinkedDirectory_StillAllowsFilesInsideIt()
    {
        // macOS `/tmp` is a link to `/private/tmp`, and container bind mounts are often the same shape.
        // If roots were only canonicalised while candidates were fully link-resolved, the two would
        // never share a prefix and a correctly-configured root would refuse every legitimate path. That
        // fails closed, so it is not a bypass — but a confinement rule that rejects everything is one
        // somebody switches off.
        var realRoot = Path.Combine(Path.GetDirectoryName(_root)!, "real-root");
        Directory.CreateDirectory(realRoot);
        var linkedRoot = Path.Combine(Path.GetDirectoryName(_root)!, "linked-root");

        try
        {
            Directory.CreateSymbolicLink(linkedRoot, realRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return; // Windows without Developer Mode.
        }

        var file = WriteFile(realRoot, "suite.yaml");

        // The root is configured by its linked name; the file is named by its real one.
        GuardWith(linkedRoot).Resolve(file).IsAllowed.Should().BeTrue();
    }

    [Fact]
    public void RootConfigured_StillAllowsARealFileBeneathARealSubdirectory()
    {
        // The other half of segment-walking: resolving every segment must not start refusing ordinary
        // nested paths. Without this, the test above is satisfied by a guard that refuses everything.
        var nested = Path.Combine(_root, "suites", "regression");
        Directory.CreateDirectory(nested);
        var file = WriteFile(nested, "suite.yaml");

        GuardWith(_root).Resolve(file).IsAllowed.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankPath_IsRefused(string path)
    {
        GuardWith(_root).Resolve(path).IsAllowed.Should().BeFalse();
    }

    [Fact]
    public void MultipleRoots_AllowsAFileInAnyOfThem()
    {
        var second = _root + "-second";
        Directory.CreateDirectory(second);
        var file = WriteFile(second, "suite.yaml");

        GuardWith(_root, second).Resolve(file).IsAllowed.Should().BeTrue();
    }

    [Fact]
    public void RootsRemovedAfterStartup_RefuseEverythingRatherThanReturningToUnconfined()
    {
        // The startup check that stops an unconfined host booting runs once. If emptying DatasetRoots at
        // runtime dropped this guard back to the permissive branch, a configuration reload would silently
        // convert a confined host into arbitrary-file-read — with no error and nothing in the log.
        var config = new AppConfig();
        config.AI.Evaluation.DatasetRoots = [_root];

        var guard = GuardOver(config, startedConfined: true);

        var inside = WriteFile(_root, "suite.yaml");
        guard.Resolve(inside).IsAllowed.Should().BeTrue("the guard starts confined and the file is inside");

        config.AI.Evaluation.DatasetRoots = [];

        guard.Resolve(inside).IsAllowed.Should().BeFalse(
            "a host that started confined must not be talked back into unconfined reads");
    }

    [Fact]
    public void SwappingOneRootForAnother_StopsAdmittingFilesUnderTheOldOne()
    {
        // The guard memoizes its link-resolved roots, because resolving them per candidate repeated the
        // same walk for every file the dataset catalog confines. That cache is keyed on the configured
        // roots, and this is the case that proves the key works: a cache that ignored a configuration
        // change would keep honouring a root an operator had REMOVED, which is a widened allowlist
        // surviving the change meant to narrow it.
        //
        // Deliberately one guard instance across both reads. Every other ratchet test builds a fresh
        // guard, so none of them ever populates the cache before the configuration moves.
        var config = new AppConfig();
        config.AI.Evaluation.DatasetRoots = [_root];

        var guard = GuardOver(config, startedConfined: true);

        var underOldRoot = WriteFile(_root, "suite.yaml");
        guard.Resolve(underOldRoot).IsAllowed.Should().BeTrue("the file is inside the configured root");

        var otherRoot = Directory.CreateDirectory(Path.Combine(_outside, "other-root")).FullName;
        var underNewRoot = WriteFile(otherRoot, "suite.yaml");
        config.AI.Evaluation.DatasetRoots = [otherRoot];

        guard.Resolve(underOldRoot).IsAllowed.Should().BeFalse(
            "the old root was removed, so nothing under it may still be admitted");
        guard.Resolve(underNewRoot).IsAllowed.Should().BeTrue(
            "the new root was added, so files under it are admitted without a restart");
    }

    [Fact]
    public void RootsAlreadyGoneWhenTheGuardIsFirstBuilt_StillRefuse()
    {
        // The case a latch computed inside the constructor would miss entirely. The guard is a lazy
        // singleton, so it is first built on the first eval dispatch — potentially long after boot, and
        // after a config reload has emptied the roots. The startup verdict has to be passed in, or the
        // guard concludes it started unconfined and reads anything.
        var config = new AppConfig();
        config.AI.Evaluation.DatasetRoots = [];

        var guard = GuardOver(config, startedConfined: true);

        var outsideFile = WriteFile(_outside, "secrets.yaml");

        guard.Resolve(outsideFile).IsAllowed.Should().BeFalse(
            "the host verified confinement at startup; the guard being built later cannot undo that");
    }

    [Fact]
    public void RootsAddedAfterStartup_TakeEffectImmediately()
    {
        // The ratchet only blocks loosening. Tightening still applies live, which is what makes reading
        // configuration per call worth doing at all.
        var config = new AppConfig();
        var guard = GuardOver(config, startedConfined: false);

        var outsideFile = WriteFile(_outside, "secrets.yaml");
        guard.Resolve(outsideFile).IsAllowed.Should().BeTrue("unconfined at construction");

        config.AI.Evaluation.DatasetRoots = [_root];

        guard.Resolve(outsideFile).IsAllowed.Should().BeFalse("adding a root must confine immediately");
    }

    [Fact]
    public void BlankRootEntry_IsIgnoredRatherThanTreatedAsAllowingEverything()
    {
        // An empty string canonicalises to the current directory. Treating it as a root would quietly
        // widen the allowlist to wherever the host happens to be running from.
        var outsideFile = WriteFile(_outside, "secrets.yaml");

        GuardWith("", _root).Resolve(outsideFile).IsAllowed.Should().BeFalse();
    }
}
