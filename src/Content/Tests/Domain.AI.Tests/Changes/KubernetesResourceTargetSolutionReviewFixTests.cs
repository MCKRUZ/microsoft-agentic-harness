using Domain.AI.Changes;
using FluentAssertions;
using Xunit;

namespace Domain.AI.Tests.Changes;

/// <summary>
/// Regression tests for the <see cref="KubernetesResourceTarget.CanonicalKey"/>
/// separator-ambiguity fix (solution review 2026-06-11, finding 16).
/// </summary>
/// <remarks>
/// Before the fix, addressing fields were joined positionally with an unescaped
/// <c>/</c> separator (<c>k8s:{clusterContext}/{apiVersion}/{scope}/{kind}/{name}</c>).
/// Because <see cref="KubernetesResourceTarget.ApiVersion"/> and
/// <see cref="KubernetesResourceTarget.ClusterContext"/> legally contain <c>/</c>, two
/// genuinely different targets could produce identical canonical keys, colliding in the
/// deterministic proposal-id hash and silently deduping one proposal away. These tests
/// pin the no-collision contract from <see cref="ChangeTarget.CanonicalKey"/>.
/// </remarks>
public sealed class KubernetesResourceTargetSolutionReviewFixTests
{
    [Fact]
    public void CanonicalKey_SlashShiftedBetweenClusterContextAndApiVersion_DoesNotCollide()
    {
        // Both targets would collapse to "k8s:prod/apps/v1/..." under a naive positional join.
        var left = new KubernetesResourceTarget(
            clusterContext: "prod",
            apiVersion: "apps/v1",
            resourceKind: "Deployment",
            @namespace: "payments",
            resourceName: "api");
        var right = new KubernetesResourceTarget(
            clusterContext: "prod/apps",
            apiVersion: "v1",
            resourceKind: "Deployment",
            @namespace: "payments",
            resourceName: "api");

        left.CanonicalKey().Should().NotBe(right.CanonicalKey());
    }

    [Fact]
    public void CanonicalKey_OverlayPathInClusterContext_DoesNotCollideWithSplitVariant()
    {
        var left = new KubernetesResourceTarget(
            clusterContext: "overlays/prod",
            apiVersion: "v1",
            resourceKind: "ConfigMap",
            @namespace: "default",
            resourceName: "settings");
        var right = new KubernetesResourceTarget(
            clusterContext: "overlays",
            apiVersion: "prod/v1",
            resourceKind: "ConfigMap",
            @namespace: "default",
            resourceName: "settings");

        left.CanonicalKey().Should().NotBe(right.CanonicalKey());
    }

    [Fact]
    public void CanonicalKey_EmptyNamespace_DoesNotCollideWithLiteralClusterNamespace()
    {
        // Cluster-scoped resource (empty namespace) versus a real namespace literally
        // named "cluster" — the old sentinel-string approach mapped both to "/cluster/".
        var clusterScoped = new KubernetesResourceTarget(
            clusterContext: "prod",
            apiVersion: "rbac.authorization.k8s.io/v1",
            resourceKind: "ClusterRole",
            @namespace: "",
            resourceName: "viewer");
        var literalClusterNamespace = new KubernetesResourceTarget(
            clusterContext: "prod",
            apiVersion: "rbac.authorization.k8s.io/v1",
            resourceKind: "ClusterRole",
            @namespace: "cluster",
            resourceName: "viewer");

        clusterScoped.CanonicalKey().Should().NotBe(literalClusterNamespace.CanonicalKey());
    }

    [Fact]
    public void CanonicalKey_IdenticalAddressingFields_ProducesIdenticalKey()
    {
        // The contract's other half: targets that mean the same thing must collide.
        var a = new KubernetesResourceTarget("prod-eastus", "apps/v1", "Deployment", "payments", "api");
        var b = new KubernetesResourceTarget("prod-eastus", "apps/v1", "Deployment", "payments", "api");

        a.CanonicalKey().Should().Be(b.CanonicalKey());
    }

    [Fact]
    public void CanonicalKey_EachFieldIsLengthPrefixed()
    {
        var target = new KubernetesResourceTarget("prod", "apps/v1", "Deployment", "payments", "api");

        // Length-prefixed encoding: 4:prod / 7:apps/v1 / 8:payments / 10:Deployment / 3:api
        target.CanonicalKey().Should().Be("k8s:4:prod/7:apps/v1/8:payments/10:Deployment/3:api");
    }

    [Fact]
    public void CanonicalKey_AnyDistinctFieldChange_ChangesKey()
    {
        var baseline = new KubernetesResourceTarget("ctx", "v1", "Pod", "ns", "name");

        baseline.CanonicalKey().Should().NotBe(
            new KubernetesResourceTarget("ctxX", "v1", "Pod", "ns", "name").CanonicalKey());
        baseline.CanonicalKey().Should().NotBe(
            new KubernetesResourceTarget("ctx", "v2", "Pod", "ns", "name").CanonicalKey());
        baseline.CanonicalKey().Should().NotBe(
            new KubernetesResourceTarget("ctx", "v1", "Service", "ns", "name").CanonicalKey());
        baseline.CanonicalKey().Should().NotBe(
            new KubernetesResourceTarget("ctx", "v1", "Pod", "nsX", "name").CanonicalKey());
        baseline.CanonicalKey().Should().NotBe(
            new KubernetesResourceTarget("ctx", "v1", "Pod", "ns", "nameX").CanonicalKey());
    }
}
