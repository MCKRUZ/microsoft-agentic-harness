using Infrastructure.Postgres.Migrations;
using Xunit;

namespace Infrastructure.Postgres.Tests;

/// <summary>
/// Covers the ledger-table name check. The name is interpolated into DDL because Postgres will not
/// accept a parameter where an identifier belongs, so this is the only thing standing between that
/// interpolation and an injection — worth proving rather than asserting in a comment.
/// </summary>
public sealed class PostgresMigrationOptionsTests
{
    [Theory]
    [InlineData("schema_migrations")]
    [InlineData("kg_schema_migrations")]
    [InlineData("m1")]
    public void Validate_PlainLowerCaseIdentifier_IsAccepted(string table)
    {
        new PostgresMigrationOptions(table, 1).Validate();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Schema_Migrations")]         // upper case would need quoting to round-trip
    [InlineData("public.schema_migrations")]  // a dot is a second identifier, not part of this one
    [InlineData("schema-migrations")]
    [InlineData("schema_migrations; DROP TABLE sessions --")]
    [InlineData("\"schema_migrations\"")]
    public void Validate_AnythingOtherThanABareIdentifier_Throws(string table)
    {
        Assert.Throws<ArgumentException>(() => new PostgresMigrationOptions(table, 1).Validate());
    }

    [Fact]
    public void Constructor_IsNotWhereValidationHappens_SoTheRunnerMustCallValidate()
    {
        // Documenting the seam rather than the behaviour: the record accepts anything, and
        // PostgresMigrationRunner's constructor is what refuses it. If validation ever moves into
        // the record this test should be deleted, not made to pass by weakening the runner.
        var options = new PostgresMigrationOptions("Not Valid", 1);

        Assert.Throws<ArgumentException>(
            () => new PostgresMigrationRunner(options, [], Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance));
    }
}
