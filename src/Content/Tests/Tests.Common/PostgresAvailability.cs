using System.Net.Sockets;

namespace Tests.Common;

/// <summary>
/// Decides whether a Postgres-backed test suite should run, skip, or fail loudly — one answer, for
/// every suite that asks.
/// </summary>
/// <remarks>
/// <para>
/// Two suites need this: <c>Infrastructure.Observability.Tests</c>'s <c>PostgresFixture</c> and
/// <c>Infrastructure.Postgres.Tests</c>'s <c>MigrationTestSchema</c>. Both had grown their own copy
/// of the connection string, the environment-variable override, the socket-error list, and the skip
/// message. That is a <em>policy</em>, not a utility, and it was stated twice with nothing that went
/// red when the two disagreed: adding a socket error, honouring a second environment variable, or
/// changing the default port would have fixed one suite and quietly left the other on the old rule.
/// </para>
/// <para>
/// <strong>The policy.</strong> Nothing listening on the default local endpoint means Postgres was
/// never provisioned, so the suite skips — that is the ordinary state of a developer machine and it
/// must not read as failure. <em>Any</em> other failure means something is genuinely wrong: a
/// reachable server rejecting the probe (wrong password, missing database, schema drift) is a
/// defect, not an absence. And when the connection string was supplied explicitly, the operator or
/// CI is asserting Postgres is there, so every failure is loud — including a refused connection,
/// which in that context means the thing that was promised is missing.
/// </para>
/// <para>
/// <strong>Why the probe itself is not here.</strong> This project has no package references at all
/// and that is deliberate — it is referenced by nearly every test project, so anything added here is
/// added everywhere. Npgsql and Xunit.SkippableFact would both have to come along to host the four
/// lines that open a connection and the one line that skips. Those four lines also genuinely differ
/// between the two callers: one keeps the data source it built, the other throws it away. What was
/// duplicated is the decision, and the decision is expressible in the framework alone.
/// </para>
/// </remarks>
public static class PostgresAvailability
{
    /// <summary>
    /// The environment variable that overrides the connection string and, by being set at all,
    /// asserts that Postgres is provisioned.
    /// </summary>
    public const string ConnectionVariable = "OBSERVABILITY_TEST_CONN";

    /// <summary>The local endpoint assumed when nothing is configured.</summary>
    public const string DefaultConnectionString =
        "Host=localhost;Port=5432;Database=observability;Username=observability;Password=observability";

    /// <summary>The connection string these suites should use.</summary>
    public static string ConnectionString =>
        Environment.GetEnvironmentVariable(ConnectionVariable) ?? DefaultConnectionString;

    /// <summary>
    /// True when the connection string came from the environment rather than the default.
    /// </summary>
    /// <remarks>
    /// The caller has asserted Postgres is present, so a connectivity failure is a real defect and
    /// must surface rather than silently disabling a hundred tests. This is what keeps CI honest:
    /// CI always sets the variable, so CI can never skip its way to green.
    /// <para>
    /// Private on purpose. It is half of <see cref="ShouldSkip"/>, and a public half is an invitation
    /// to re-derive the skip decision at a call site — which is the exact duplication this type was
    /// extracted to end.
    /// </para>
    /// </remarks>
    private static bool IsExplicitlyConfigured =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionVariable));

    /// <summary>
    /// Why a suite was skipped. Shared so both suites tell an operator the same thing.
    /// </summary>
    public static string SkipReason =>
        $"Postgres is not provisioned for this run (set {ConnectionVariable} or start a local " +
        "Postgres on localhost:5432). The test is skipped rather than reported as a silent pass.";

    /// <summary>
    /// Whether a probe failure means "Postgres was never provisioned here" — the only case a suite
    /// may skip on.
    /// </summary>
    /// <param name="exception">The failure the connection probe threw.</param>
    /// <returns><c>true</c> to skip the suite; <c>false</c> to let the failure surface.</returns>
    /// <remarks>
    /// Intended as an exception filter, so a failure that is not an absence never enters the catch
    /// block and keeps its original stack trace:
    /// <c>catch (Exception ex) when (PostgresAvailability.ShouldSkip(ex))</c>.
    /// </remarks>
    public static bool ShouldSkip(Exception exception) =>
        !IsExplicitlyConfigured && IsServerAbsent(exception);

    /// <summary>
    /// Whether a failure means nothing is listening at all, as opposed to something answering and
    /// refusing.
    /// </summary>
    /// <param name="exception">The failure to inspect, including its inner exceptions.</param>
    /// <returns><c>true</c> when no server is reachable.</returns>
    /// <remarks>
    /// Walks the inner-exception chain because Npgsql wraps the <see cref="SocketException"/>. The
    /// listed codes are the ways "nothing is there" arrives; everything else — authentication, a
    /// missing database, a schema problem — is a reachable server saying no, which must never be
    /// masked as an unavailable fixture.
    /// </remarks>
    public static bool IsServerAbsent(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SocketException socket &&
                socket.SocketErrorCode is SocketError.ConnectionRefused
                    or SocketError.HostNotFound
                    or SocketError.HostUnreachable
                    or SocketError.NetworkUnreachable
                    or SocketError.TimedOut)
            {
                return true;
            }
        }

        return false;
    }
}
