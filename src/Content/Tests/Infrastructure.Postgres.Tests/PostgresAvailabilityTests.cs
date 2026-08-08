using System.Net.Sockets;
using Npgsql;
using Tests.Common;
using Xunit;

namespace Infrastructure.Postgres.Tests;

/// <summary>
/// Covers the rule that decides whether a Postgres suite skips or fails — which, until it was
/// centralised, was written twice and tested nowhere.
/// </summary>
/// <remarks>
/// <para>
/// The consequence of getting this wrong is asymmetric and that is why it is worth its own tests.
/// Too strict and a developer with no Postgres sees a wall of red that means nothing. Too loose and
/// a reachable server rejecting every connection — wrong password, missing database, a schema that
/// never got created — is reported as "not provisioned", and roughly a hundred integration tests
/// vanish from the run while the summary still says green. The second failure is the dangerous one
/// because it looks exactly like success.
/// </para>
/// <para>
/// <strong>These tests never set the environment variable.</strong> It is process-global, xUnit runs
/// classes in parallel, and the suites in this assembly open real connections using the value it
/// controls — a test that flipped it would hand another test a bogus connection string, producing a
/// failure with no relationship to the code under test. So the environment-dependent half of
/// <c>ShouldSkip</c> is left to the integration suites that exercise it for real, and the classifier
/// underneath, which is pure and is where the actual subtlety lives, is covered exhaustively here.
/// </para>
/// </remarks>
public sealed class PostgresAvailabilityTests
{
    [Theory]
    [InlineData(SocketError.ConnectionRefused)]
    [InlineData(SocketError.HostNotFound)]
    [InlineData(SocketError.HostUnreachable)]
    [InlineData(SocketError.NetworkUnreachable)]
    [InlineData(SocketError.TimedOut)]
    public void NothingListening_CountsAsAbsent(SocketError code) =>
        Assert.True(PostgresAvailability.IsServerAbsent(new SocketException((int)code)));

    /// <summary>
    /// A server that answers and then refuses is present, not absent.
    /// </summary>
    /// <remarks>
    /// <c>ConnectionReset</c> and <c>AccessDenied</c> are the interesting pair: both are socket
    /// errors, so a check that merely asked "is there a SocketException in here" would call them
    /// absence and skip the suite. Something was listening in both cases.
    /// </remarks>
    [Theory]
    [InlineData(SocketError.ConnectionReset)]
    [InlineData(SocketError.AccessDenied)]
    [InlineData(SocketError.AddressAlreadyInUse)]
    public void AServerThatAnswersAndRefuses_IsNotAbsent(SocketError code) =>
        Assert.False(PostgresAvailability.IsServerAbsent(new SocketException((int)code)));

    [Fact]
    public void AnAuthenticationFailure_IsNotAbsent() =>
        Assert.False(PostgresAvailability.IsServerAbsent(
            new PostgresException("password authentication failed", "FATAL", "FATAL", "28P01")));

    /// <summary>
    /// The socket error is found however deeply Npgsql has wrapped it.
    /// </summary>
    /// <remarks>
    /// This is the whole reason the check walks the chain rather than pattern-matching the exception
    /// it is handed. Npgsql surfaces a refused connection as an <c>NpgsqlException</c> wrapping the
    /// <see cref="SocketException"/>, and the depth is an implementation detail that has changed
    /// across versions — so a check written against one level of nesting would start skipping
    /// suites, or start failing them, on a package bump and for no other reason.
    /// </remarks>
    [Fact]
    public void TheSocketErrorIsFoundHoweverDeeplyItIsWrapped()
    {
        var buried = new InvalidOperationException(
            "outer",
            new NpgsqlException(
                "Failed to connect",
                new SocketException((int)SocketError.ConnectionRefused)));

        Assert.True(PostgresAvailability.IsServerAbsent(buried));
    }

    [Fact]
    public void AFailureWithNoSocketErrorAnywhere_IsNotAbsent() =>
        Assert.False(PostgresAvailability.IsServerAbsent(
            new InvalidOperationException("outer", new TimeoutException("inner"))));

    /// <summary>
    /// The skip message names the override, so an operator reading it can act on it.
    /// </summary>
    /// <remarks>
    /// Not decoration: the message is the only place a developer learns that the suite is opt-in and
    /// how to opt in. Binding it to the variable's own constant means renaming the variable cannot
    /// leave the instructions pointing at a name that no longer exists.
    /// </remarks>
    [Fact]
    public void TheSkipReason_TellsTheReaderHowToProvisionPostgres()
    {
        Assert.Contains(PostgresAvailability.ConnectionVariable, PostgresAvailability.SkipReason,
            StringComparison.Ordinal);
        Assert.Contains("skipped rather than reported as a silent pass", PostgresAvailability.SkipReason,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheDefaultConnectionString_TargetsLocalhost() =>
        Assert.Contains("Host=localhost", PostgresAvailability.DefaultConnectionString,
            StringComparison.Ordinal);
}
