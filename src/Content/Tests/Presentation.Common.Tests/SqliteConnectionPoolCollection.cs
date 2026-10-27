using Xunit;

namespace Presentation.Common.Tests;

/// <summary>
/// Serialises every test class in this assembly that calls <c>SqliteConnection.ClearAllPools()</c>, so a class cleaning
/// up cannot close the connections another class is still using (#924).
/// </summary>
/// <remarks>
/// <para>
/// <c>ClearAllPools</c> closes every pooled SQLite connection in the process, not only the caller's, and xUnit runs test
/// classes in parallel. Closing the last connection to a database checkpoints its write-ahead log and deletes the
/// <c>-wal</c> file, so a sibling's clean-up could remove the file <c>ErasureReachesEveryStoreEndToEndTests</c> scans
/// for erased bytes, between its erase and its scan. The end-to-end classes here also share the process-wide in-memory
/// keyword index, which is the other interference #924 records.
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
