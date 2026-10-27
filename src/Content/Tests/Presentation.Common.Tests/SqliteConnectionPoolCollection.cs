using Xunit;

namespace Presentation.Common.Tests;

/// <summary>
/// Serialises every test class in this assembly that calls <c>SqliteConnection.ClearAllPools()</c>, so a class cleaning
/// up cannot close the connections another class is still using (#924).
/// </summary>
/// <remarks>
/// <para>
/// <c>ClearAllPools</c> closes every idle pooled SQLite connection in the process, not only the caller's, and xUnit runs
/// test classes in parallel. The end-to-end classes here rely on idle pooled connections staying open:
/// <c>ErasureReachesEveryStoreEndToEndTests</c> scans each database's write-ahead log after an erasure, and closing the
/// last connection to a database checkpoints that log and deletes the <c>-wal</c> file between the erase and the scan.
/// The process-wide in-memory keyword index they share is dropped the same way when its last connection closes, the
/// other interference #924 records. The classes that clear the pools are, almost all, these same end-to-end classes.
/// </para>
/// <para>
/// A <c>DisableParallelization</c> collection runs after the parallel ones, one test at a time, so nothing else is
/// running when one of these classes clears the pools. <c>EveryPoolClearingClassIsSerialisedTests</c> keeps a new
/// pool-clearing class from being left out.
/// </para>
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SqliteConnectionPoolCollection
{
    /// <summary>The collection name. Apply with <c>[Collection(SqliteConnectionPoolCollection.Name)]</c>.</summary>
    public const string Name = "SqliteConnectionPool";
}
