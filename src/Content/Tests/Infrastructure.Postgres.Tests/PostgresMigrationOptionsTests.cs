using Infrastructure.Postgres.Migrations;
using Xunit;

namespace Infrastructure.Postgres.Tests;

/// <summary>
/// Covers the ledger-table name check. The name is interpolated into DDL because Postgres will not
/// accept a parameter where an identifier belongs, so this is the only thing standing between that
/// interpolation and an injection — worth proving rather than asserting in a comment.
/// </summary>
/// <remarks>
/// These target the constructor. They used to call a separate <c>Validate()</c>, alongside a third
/// test whose whole job was to document that the record could be constructed invalid and that one
/// caller remembered to check it. That test was the argument for deleting the seam: a record that
/// cannot hold a bad value proves strictly more than a record plus a method someone must remember.
/// </remarks>
public sealed class PostgresMigrationOptionsTests
{
    [Theory]
    [InlineData("obs_schema_migrations")]
    [InlineData("kg_schema_migrations")]
    [InlineData("m1")]
    public void PlainLowerCaseIdentifier_IsAccepted(string table)
    {
        Assert.Equal(table, new PostgresMigrationOptions(table, 1).LedgerTable);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Schema_Migrations")]         // upper case would need quoting to round-trip
    [InlineData("public.schema_migrations")]  // a dot is a second identifier, not part of this one
    [InlineData("schema-migrations")]
    [InlineData("schema_migrations; DROP TABLE sessions --")]
    [InlineData("\"schema_migrations\"")]
    public void AnythingOtherThanABareIdentifier_IsRefusedAtConstruction(string table)
    {
        Assert.Throws<ArgumentException>(() => new PostgresMigrationOptions(table, 1));
    }

    [Fact]
    public void AnInvalidNameCannotReachTheRunnerAtAll()
    {
        // The runner no longer validates, because it no longer can be handed anything invalid.
        // Constructing the options is where it fails, one frame earlier than it used to.
        Assert.Throws<ArgumentException>(() => new PostgresMigrationRunner(
            new PostgresMigrationOptions("Not Valid", 1),
            [],
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance));
    }
}
