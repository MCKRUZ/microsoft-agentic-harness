using FluentAssertions;
using Tests.Common;
using Xunit;

namespace Presentation.AuditArchiver.Tests;

/// <summary>
/// Every call one of this host's assemblies makes into another still exists in the version of that
/// assembly the host loads. Package skew surfaces as a <see cref="MissingMethodException"/> the first time
/// the call is made, and neither the compiler nor the container-validation sweep can see it.
/// </summary>
/// <remarks>
/// See <see cref="PackageSkewGuard"/> for how the scan works and what it cannot see, and
/// <see cref="KnownHostSkews"/> for the accepted exceptions and why each is accepted.
/// </remarks>
public sealed class PackageSkewTests
{
    [Fact]
    public void EveryCrossAssemblyReference_ResolvesAgainstTheVersionTheHostLoads()
    {
        var problems = PackageSkewGuard.Problems("Presentation.AuditArchiver", KnownHostSkews.AuditArchiver);

        problems.Should().BeEmpty(
            "a reference that does not resolve is a MissingMethodException waiting for the first caller; "
            + "fix the skew, or add a KnownHostSkews entry with the reason it is accepted");
    }
}
