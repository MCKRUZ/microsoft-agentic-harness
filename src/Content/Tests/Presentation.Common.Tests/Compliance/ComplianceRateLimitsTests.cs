using System.Security.Claims;
using System.Threading.RateLimiting;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Presentation.Common.Compliance;
using Xunit;

namespace Presentation.Common.Tests.Compliance;

/// <summary>
/// The compliance limits every host applies (#1003), measured on the limiter itself: per caller and per endpoint, a
/// host-wide cap on copies assembled at once, and a request turned away because the host is busy costs the person nothing.
/// </summary>
public sealed class ComplianceRateLimitsTests
{
    private static HttpContext Post(string path, string caller)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = path;
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("oid", caller)], "Test"));
        return context;
    }

    private static bool Acquired(PartitionedRateLimiter<HttpContext> limiter, HttpContext context, List<RateLimitLease> held)
    {
        var lease = limiter.AttemptAcquire(context);
        held.Add(lease);
        return lease.IsAcquired;
    }

    [Fact]
    public void SelfServiceErasure_IsCappedPerPerson_AndEachPersonHasTheirOwnAllowance()
    {
        using var limiter = ComplianceRateLimits.Create();
        var held = new List<RateLimitLease>();

        Enumerable.Range(0, ComplianceRateLimits.SelfServicePerHour)
            .Select(_ => Acquired(limiter, Post("/api/compliance/erase-my-data", "alice"), held)).ToList()
            .Should().AllBeEquivalentTo(true);

        Acquired(limiter, Post("/api/compliance/erase-my-data", "alice"), held).Should().BeFalse();
        Acquired(limiter, Post("/api/compliance/erase-my-data", "bob"), held).Should().BeTrue();
        Acquired(limiter, Post("/api/compliance/export-my-data", "alice"), held).Should().BeTrue(
            "each endpoint has its own allowance");
    }

    [Fact]
    public void Exports_BeyondTheHostWideLimit_AreRefused_WhoeverAsks()
    {
        using var limiter = ComplianceRateLimits.Create();
        var held = new List<RateLimitLease>();
        for (var i = 0; i < ComplianceRateLimits.ConcurrentExportLimit; i++)
            Acquired(limiter, Post("/api/compliance/export-my-data", $"person-{i}"), held).Should().BeTrue();

        Acquired(limiter, Post("/api/compliance/exports", "operator-1"), held).Should().BeFalse(
            "an operator's copy holds the same whole data and shares the same budget");
    }

    [Fact]
    public void AnExportRefusedBecauseTheHostIsBusy_DoesNotSpendThePersonsHourlyAllowance()
    {
        using var limiter = ComplianceRateLimits.Create();
        var running = new List<RateLimitLease>();
        for (var i = 0; i < ComplianceRateLimits.ConcurrentExportLimit; i++)
            Acquired(limiter, Post("/api/compliance/export-my-data", $"person-{i}"), running).Should().BeTrue();

        var refused = new List<RateLimitLease>();
        for (var i = 0; i < ComplianceRateLimits.SelfServicePerHour; i++)
            Acquired(limiter, Post("/api/compliance/export-my-data", "carol"), refused).Should().BeFalse();
        running.ForEach(l => l.Dispose());

        Acquired(limiter, Post("/api/compliance/export-my-data", "carol"), refused).Should().BeTrue(
            "five tries while the host was busy must not lock the person out for an hour");
    }

    [Fact]
    public async Task AnExportRefusedByThePersonsWindow_GivesItsExportSlotBack()
    {
        // The host-wide limit runs first and takes a slot before the person's window is asked; when the window refuses,
        // the slot must come back on both acquire paths the middleware uses, or one person over their allowance could
        // hold every slot and stop everyone else's copies.
        using var limiter = ComplianceRateLimits.Create();
        var held = new List<RateLimitLease>();
        for (var i = 0; i < ComplianceRateLimits.SelfServicePerHour; i++)
        {
            // Each copy finishes before the next starts, so only the person's window is spent.
            Acquired(limiter, Post("/api/compliance/export-my-data", "dave"), held).Should().BeTrue();
            held[^1].Dispose();
        }

        for (var i = 0; i < ComplianceRateLimits.ConcurrentExportLimit + 1; i++)
        {
            Acquired(limiter, Post("/api/compliance/export-my-data", "dave"), held).Should().BeFalse();
            var waited = await limiter.AcquireAsync(Post("/api/compliance/export-my-data", "dave"));
            held.Add(waited);
            waited.IsAcquired.Should().BeFalse();
        }

        Enumerable.Range(0, ComplianceRateLimits.ConcurrentExportLimit)
            .Select(i => Acquired(limiter, Post("/api/compliance/export-my-data", $"other-{i}"), held)).ToList()
            .Should().AllBeEquivalentTo(true, "every refused request must have handed its slot back");
        held.ForEach(l => l.Dispose());
    }

    [Fact]
    public void OtherPaths_AreNotLimited()
    {
        using var limiter = ComplianceRateLimits.Create();
        var held = new List<RateLimitLease>();

        Enumerable.Range(0, 100).Select(_ => Acquired(limiter, Post("/api/workflows", "alice"), held)).ToList()
            .Should().AllBeEquivalentTo(true);
    }
}
