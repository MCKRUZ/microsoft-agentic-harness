using Domain.AI.Changes;
using FluentAssertions;
using Xunit;

namespace Domain.AI.Tests.Changes;

/// <summary>
/// Tests for the <see cref="ChangeTarget"/> polymorphic hierarchy — verifies each
/// concrete subtype exposes its discriminator correctly, builds a sensible display
/// name, and produces a deterministic canonical key suitable for the proposal-id hash
/// (Step 2).
/// </summary>
public sealed class ChangeTargetTests
{
    [Fact]
    public void GitRepoTarget_Construction_SetsKindAndProperties()
    {
        var target = new GitRepoTarget(
            repoUrl: "https://github.com/org/repo",
            branch: "main",
            headSha: "abc123",
            workingPath: "src/app");

        target.Kind.Should().Be(ChangeTargetKind.GitRepo);
        target.RepoUrl.Should().Be("https://github.com/org/repo");
        target.Branch.Should().Be("main");
        target.HeadSha.Should().Be("abc123");
        target.WorkingPath.Should().Be("src/app");
        target.DisplayName.Should().Be("https://github.com/org/repo#main");
    }

    [Fact]
    public void GitRepoTarget_CanonicalKey_IncludesUrlBranchAndHead()
    {
        var target = new GitRepoTarget("https://github.com/org/repo", "main", "abc123", "src");

        target.CanonicalKey().Should().Be("git:https://github.com/org/repo#main@abc123:src");
    }

    [Fact]
    public void GitRepoTarget_CanonicalKey_WithoutHeadSha_UsesHeadLiteral()
    {
        var target = new GitRepoTarget("https://github.com/org/repo", "main");

        target.CanonicalKey().Should().Be("git:https://github.com/org/repo#main@HEAD:");
    }

    [Fact]
    public void GitRepoTarget_DifferentHeadSha_ProducesDifferentCanonicalKey()
    {
        var a = new GitRepoTarget("repo", "main", "sha1");
        var b = new GitRepoTarget("repo", "main", "sha2");

        a.CanonicalKey().Should().NotBe(b.CanonicalKey());
    }

    [Fact]
    public void GitRepoTarget_DisplayName_WithEmptyInputs_FallsBackToPlaceholder()
    {
        var target = new GitRepoTarget("", "");

        target.DisplayName.Should().Be("(unspecified git target)");
    }

    [Fact]
    public void KubernetesResourceTarget_Construction_SetsKindAndProperties()
    {
        var target = new KubernetesResourceTarget(
            clusterContext: "prod-eastus",
            apiVersion: "apps/v1",
            resourceKind: "Deployment",
            @namespace: "payments",
            resourceName: "api");

        target.Kind.Should().Be(ChangeTargetKind.KubernetesResource);
        target.ClusterContext.Should().Be("prod-eastus");
        target.ApiVersion.Should().Be("apps/v1");
        target.ResourceKind.Should().Be("Deployment");
        target.Namespace.Should().Be("payments");
        target.ResourceName.Should().Be("api");
        target.DisplayName.Should().Be("prod-eastus/payments/Deployment/api");
    }

    [Fact]
    public void KubernetesResourceTarget_CanonicalKey_IncludesAllAddressingFields()
    {
        var target = new KubernetesResourceTarget(
            "prod-eastus", "apps/v1", "Deployment", "payments", "api");

        // Each field is length-prefixed ({len}:{value}) so that '/' inside a field
        // (e.g. apps/v1) can never be confused with the field separator.
        target.CanonicalKey().Should().Be(
            "k8s:11:prod-eastus/7:apps/v1/8:payments/10:Deployment/3:api");
    }

    [Fact]
    public void KubernetesResourceTarget_ClusterScopedResource_EncodesEmptyNamespaceAsZeroLength()
    {
        var target = new KubernetesResourceTarget(
            "prod-eastus", "rbac.authorization.k8s.io/v1", "ClusterRole", "", "viewer");

        // Empty namespace is encoded as a zero-length field (0:), distinct from a
        // namespace literally named "cluster" — the display name still uses the
        // human-readable "cluster" sentinel.
        target.CanonicalKey().Should().Contain("/0:/");
        target.DisplayName.Should().Be("prod-eastus/cluster/ClusterRole/viewer");
    }

    [Fact]
    public void IacDeploymentTarget_Construction_SetsKindAndProperties()
    {
        var target = new IacDeploymentTarget(
            backend: "terraform",
            deploymentName: "network-prod",
            modulePath: "modules/network/main.tf",
            environment: "prod");

        target.Kind.Should().Be(ChangeTargetKind.IacDeployment);
        target.Backend.Should().Be("terraform");
        target.DeploymentName.Should().Be("network-prod");
        target.ModulePath.Should().Be("modules/network/main.tf");
        target.Environment.Should().Be("prod");
        target.DisplayName.Should().Be("terraform/prod/network-prod");
    }

    [Fact]
    public void IacDeploymentTarget_CanonicalKey_IncludesBackendEnvDeploymentModule()
    {
        var target = new IacDeploymentTarget("bicep", "rg-payments", "infra/main.bicep", "prod");

        target.CanonicalKey().Should().Be("iac:bicep:prod:rg-payments:infra/main.bicep");
    }

    [Fact]
    public void TargetsOfDifferentKinds_HaveDistinctCanonicalKeys()
    {
        var git = new GitRepoTarget("repo", "main");
        var k8s = new KubernetesResourceTarget("ctx", "v1", "Pod", "ns", "name");
        var iac = new IacDeploymentTarget("terraform", "dep", "main.tf", "prod");

        git.CanonicalKey().Should().NotBe(k8s.CanonicalKey());
        git.CanonicalKey().Should().NotBe(iac.CanonicalKey());
        k8s.CanonicalKey().Should().NotBe(iac.CanonicalKey());
    }

    [Fact]
    public void ChangeTarget_KindIsImmutable_AndBackedBySubtype()
    {
        ChangeTarget t = new GitRepoTarget("repo", "main");

        t.Kind.Should().Be(ChangeTargetKind.GitRepo);
    }
}
