using Domain.AI.Bundles;
using FluentAssertions;
using Xunit;

namespace Domain.AI.Tests.Bundles;

/// <summary>
/// Tests for <see cref="CapabilityEnvelope.IsBundleOwnedMcpServer"/> — issue #376. The record's own
/// remarks document an invariant (<c>BundleOwnedMcpServers</c> is always a subset of
/// <c>AllowedMcpServers</c>) that nothing at construction time enforces, because the single production
/// writer uses a <c>with</c> expression, which bypasses any factory. This class pins down the two ways
/// the lists can drift apart and treats them deliberately asymmetrically: a name present only in
/// <c>BundleOwnedMcpServers</c> is now also refused by <see cref="CapabilityEnvelope.IsBundleOwnedMcpServer"/>
/// (making that predicate consistent with <see cref="CapabilityEnvelope.GrantsMcpServer"/>, which already
/// refused it), while a name present only in <c>AllowedMcpServers</c> is UNCHANGED and un-catchable by any
/// check on this type — see <see cref="IsBundleOwnedMcpServer_NamePresentOnlyInAllowedList_ReturnsFalse"/>
/// for why, and why that direction's real guard lives on the writer instead.
/// </summary>
public sealed class CapabilityEnvelopeTests
{
    [Fact]
    public void IsBundleOwnedMcpServer_NamePresentInBothLists_ReturnsTrue()
    {
        var envelope = new CapabilityEnvelope
        {
            AllowedMcpServers = ["bundle-1:server"],
            BundleOwnedMcpServers = ["bundle-1:server"],
        };

        envelope.IsBundleOwnedMcpServer("bundle-1:server").Should().BeTrue();
    }

    [Fact]
    public void IsBundleOwnedMcpServer_NamePresentOnlyInBundleOwnedList_ReturnsFalse()
    {
        // The dangerous direction the invariant exists to prevent, inverted: here the writer forgot to
        // grant the server at all (AllowedMcpServers is empty), so the server cannot be contacted
        // regardless — GrantsMcpServer already refuses it. This asserts IsBundleOwnedMcpServer agrees
        // rather than claiming ownership of a server nothing actually granted.
        var envelope = new CapabilityEnvelope
        {
            AllowedMcpServers = [],
            BundleOwnedMcpServers = ["bundle-1:server"],
        };

        envelope.IsBundleOwnedMcpServer("bundle-1:server").Should().BeFalse();
        envelope.GrantsMcpServer("bundle-1:server").Should().BeFalse();
    }

    [Fact]
    public void IsBundleOwnedMcpServer_NamePresentOnlyInAllowedList_ReturnsFalse()
    {
        // The direction the record's own remarks call out as the real danger: a writer granted the
        // server (AllowedMcpServers) but never recorded it as bundle-owned. This behaviour is UNCHANGED
        // by #376 — checking BundleOwnedMcpServers alone already returned false here, since the name is
        // simply absent from that list, and no additional check on this type could ever recover it. This
        // pins that fail-closed outcome so it stays proven: such a server is treated as host-trusted, not
        // bundle-owned, so its tools never get the namespacing that keeps a bundle's own tool from
        // impersonating a real host tool by name. The actual guard against this shape ever occurring is
        // the writer test on RunBundleCommandHandler.WithBundleOwnedMcpServers, not this predicate.
        var envelope = new CapabilityEnvelope
        {
            AllowedMcpServers = ["bundle-1:server"],
            BundleOwnedMcpServers = [],
        };

        envelope.IsBundleOwnedMcpServer("bundle-1:server").Should().BeFalse();
        envelope.GrantsMcpServer("bundle-1:server").Should().BeTrue("the server IS granted — just not as bundle-owned");
    }

    [Fact]
    public void IsBundleOwnedMcpServer_NamePresentInNeitherList_ReturnsFalse()
    {
        var envelope = new CapabilityEnvelope
        {
            AllowedMcpServers = ["other-server"],
            BundleOwnedMcpServers = ["other-server"],
        };

        envelope.IsBundleOwnedMcpServer("bundle-1:server").Should().BeFalse();
    }

    [Fact]
    public void IsBundleOwnedMcpServer_IsCaseInsensitive_LikeEveryOtherGrantsPredicateOnThisRecord()
    {
        var envelope = new CapabilityEnvelope
        {
            AllowedMcpServers = ["Bundle-1:Server"],
            BundleOwnedMcpServers = ["Bundle-1:Server"],
        };

        envelope.IsBundleOwnedMcpServer("bundle-1:server").Should().BeTrue();
    }
}
