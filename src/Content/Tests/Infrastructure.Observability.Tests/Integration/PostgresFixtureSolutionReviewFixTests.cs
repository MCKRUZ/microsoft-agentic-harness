using FluentAssertions;
using Xunit;

namespace Infrastructure.Observability.Tests.Integration;

/// <summary>
/// Regression tests for the solution-review finding that <see cref="PostgresFixture"/> swallowed
/// every probe failure into <c>IsAvailable=false</c>, letting the entire Observability integration
/// suite report green when Postgres was misconfigured or absent in an environment that expected it.
/// <para>
/// These run in their own non-parallel collection because they mutate the process-wide
/// <c>OBSERVABILITY_TEST_CONN</c> environment variable that the production fixture reads at
/// construction time.
/// </para>
/// </summary>
[Collection("PostgresFixtureFix")]
public sealed class PostgresFixtureSolutionReviewFixTests
{
    private const string EnvVar = "OBSERVABILITY_TEST_CONN";

    /// <summary>
    /// A localhost endpoint on a port nothing listens on yields a connection-refused
    /// <see cref="System.Net.Sockets.SocketException"/>, which is the "server absent" case.
    /// </summary>
    private const string UnreachableLocalConnection =
        "Host=localhost;Port=1;Database=observability;Username=observability;Password=observability;" +
        "Timeout=2;Command Timeout=2";

    [Fact]
    public async Task InitializeAsync_DefaultEndpointServerAbsent_DisablesWithoutThrowing()
    {
        // Arrange — OBSERVABILITY_TEST_CONN is unset (the "Postgres not provisioned locally" case).
        // The fixture falls back to the localhost default and we cannot point it elsewhere, so this
        // exercises the real default endpoint: it is either running (skip — see below) or refusing
        // connections. Either way it must NOT throw and must NOT crash the fixture.
        var previous = Environment.GetEnvironmentVariable(EnvVar);
        Environment.SetEnvironmentVariable(EnvVar, null);
        var fixture = new PostgresFixture();
        try
        {
            // Act / Assert — with no explicit config, a server-absent default endpoint disables the
            // suite quietly rather than throwing.
            var act = async () => await fixture.InitializeAsync();
            await act.Should().NotThrowAsync(
                "an unconfigured environment with no local Postgres must opt the suite out, not blow up");
        }
        finally
        {
            await fixture.DisposeAsync();
            Environment.SetEnvironmentVariable(EnvVar, previous);
        }
    }

    [Fact]
    public void SkipIfUnavailable_PostgresAbsent_ReportsSkippedNotPassed()
    {
        // Arrange — a fresh, uninitialized fixture has IsAvailable=false, modelling the
        // "Postgres not provisioned" run. The bug under test: the per-test guard used a bare
        // `if (!IsAvailable) return;`, so xUnit reported a green PASS with zero assertions. The
        // honest behaviour is to skip the test, which xUnit signals by throwing SkipException.
        var fixture = new PostgresFixture();

        // Act
        var act = () => fixture.SkipIfUnavailable();

        // Assert — old behaviour silently continued (no throw → test "passes"); the fix throws the
        // xUnit skip exception so the runner records a skip rather than a pass.
        var thrown = act.Should().Throw<Exception>(
            "an unavailable Postgres must skip the test, not let it report a silent green pass")
            .Which;
        thrown.GetType().FullName.Should().EndWith(
            "SkipException",
            "the guard must produce an xUnit *skip*, not an ordinary failure or a pass-through");
    }

    [SkippableFact]
    public async Task SkipIfUnavailable_PostgresAvailable_DoesNotThrow()
    {
        // Arrange — initialize against the real default endpoint. This test only has something to
        // assert when Postgres is actually reachable; otherwise it honestly skips itself rather than
        // asserting nothing.
        var previous = Environment.GetEnvironmentVariable(EnvVar);
        Environment.SetEnvironmentVariable(EnvVar, null);
        var fixture = new PostgresFixture();
        try
        {
            await fixture.InitializeAsync();
            Skip.IfNot(
                fixture.IsAvailable,
                "No local Postgres reachable — the available-path assertion is not exercisable here.");

            // Act / Assert — when available, the guard must be a no-op so the real test body runs.
            var act = () => fixture.SkipIfUnavailable();
            act.Should().NotThrow("an available Postgres must let the test proceed");
        }
        finally
        {
            await fixture.DisposeAsync();
            Environment.SetEnvironmentVariable(EnvVar, previous);
        }
    }

    [Fact]
    public async Task InitializeAsync_ExplicitConnectionUnreachable_ThrowsInsteadOfSilentlyPassing()
    {
        // Arrange — OBSERVABILITY_TEST_CONN is set to an unreachable endpoint, asserting Postgres
        // SHOULD be present. The old catch-all swallowed the connection-refused exception and set
        // IsAvailable=false, turning ~100 integration tests green. The env var must remain set
        // through InitializeAsync because the fixture re-reads it to detect explicit configuration.
        var previous = Environment.GetEnvironmentVariable(EnvVar);
        Environment.SetEnvironmentVariable(EnvVar, UnreachableLocalConnection);
        var fixture = new PostgresFixture();
        try
        {
            // Act
            var act = async () => await fixture.InitializeAsync();

            // Assert — must surface loudly so CI fails instead of reporting a silent pass.
            await act.Should().ThrowAsync<Exception>(
                "an explicitly configured but unreachable Postgres is a real defect, not an opt-out");
            fixture.IsAvailable.Should().BeFalse();
        }
        finally
        {
            await fixture.DisposeAsync();
            Environment.SetEnvironmentVariable(EnvVar, previous);
        }
    }
}

/// <summary>
/// Dedicated collection so the env-var-mutating regression tests above do not run in parallel with
/// the shared <c>Postgres</c> collection fixture (which reads the same environment variable).
/// </summary>
[CollectionDefinition("PostgresFixtureFix", DisableParallelization = true)]
public sealed class PostgresFixtureFixCollection;
