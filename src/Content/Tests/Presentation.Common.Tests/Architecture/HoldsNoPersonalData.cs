namespace Presentation.Common.Tests.Architecture;

/// <summary>
/// Types that write to storage but hold no personal data, each with the reason and the exact storage the scan saw
/// when the reason was written (#899). Read by <see cref="EveryPersonalDataStoreIsDeclaredTests"/>.
/// </summary>
/// <remarks>
/// Keyed by the scan's own key, <c>Assembly/Namespace.Type</c>: two hosts each declare a global <c>Program</c>. When the
/// guard reports an entry whose evidence changed, re-read the type before copying the new evidence in: the change is
/// exactly what the entry was not written to excuse.
/// </remarks>
internal static class HoldsNoPersonalData
{
    /// <summary>Why a writer holds no personal data, and what the scan saw it do.</summary>
    internal sealed class Entry(string reason, params string[] evidence)
    {
        /// <summary>The reason a reviewer accepted.</summary>
        public string Reason { get; } = reason;

        /// <summary>Every reason the scan required the type when the entry was written.</summary>
        public IReadOnlyList<string> Evidence { get; } = evidence;
    }

    /// <summary>The entries, by scan key.</summary>
    public static readonly IReadOnlyDictionary<string, Entry> Entries = new Dictionary<string, Entry>(StringComparer.Ordinal)
    {
        // Secure delete (#900): they set pragmas on connections their callers own and touch no row data.
        ["Infrastructure.AI/Infrastructure.AI.Persistence.SecureDeleteConnectionInterceptor"] = new(
            "Turns SQLite secure delete on for each connection an Entity Framework context opens; it reads and writes no rows.",
            "calls channel Infrastructure.AI.RAG.Sqlite.SqliteSecureDelete"),
        ["Infrastructure.AI/Infrastructure.AI.Extensions.SqliteDatabaseFacadeExtensions"] = new(
            "Runs a write-ahead-log checkpoint after an erasure on the calling store's database; it reads and writes no rows.",
            "calls channel Infrastructure.AI.RAG.Sqlite.SqliteSecureDelete"),

        // Retention markers (#901): they record which folder a store owns, nothing about anyone.
        ["Application.Common/Application.Common.Logging.LogRunFolder"] = new(
            "Writes the marker at a log run's root naming the run id the caller chose, so the retention sweep can tell the providers' run folders from other content; the log text itself is written by the providers.",
            "System.IO.FileStream::.ctor"),
        // Schema, registration and client construction: they touch a storage API but hold no rows.
        ["Infrastructure.AI/Infrastructure.AI.Persistence.SqliteAdditiveSchemaReconciler"] = new(
            "Schema maintenance only: adds missing tables, columns and indexes to existing SQLite databases and never reads or writes row data.",
            "System.Data.Common.DbCommand::CreateParameter",
            "System.Data.Common.DbCommand::ExecuteNonQuery",
            "System.Data.Common.DbCommand::ExecuteReader",
            "System.Data.Common.DbCommand::get_Parameters",
            "System.Data.Common.DbCommand::set_CommandText",
            "System.Data.Common.DbConnection::Close",
            "System.Data.Common.DbConnection::CreateCommand",
            "System.Data.Common.DbConnection::Open",
            "System.Data.Common.DbConnection::get_State",
            "System.Data.Common.DbDataReader::GetString",
            "System.Data.Common.DbDataReader::Read",
            "System.Data.Common.DbParameter::set_ParameterName",
            "System.Data.Common.DbParameter::set_Value",
            "System.Data.Common.DbParameterCollection::Add"),
        ["Infrastructure.Postgres/Infrastructure.Postgres.Migrations.PostgresMigrationRunner"] = new(
            "Schema migrations: applies DDL scripts and records only each script's id, checksum and time in a ledger table.",
            "Npgsql.NpgsqlCommand::.ctor",
            "Npgsql.NpgsqlCommand::ExecuteReaderAsync",
            "Npgsql.NpgsqlCommand::get_Parameters",
            "Npgsql.NpgsqlConnection::BeginTransactionAsync",
            "Npgsql.NpgsqlParameterCollection::AddWithValue",
            "System.Data.Common.DbCommand::DisposeAsync",
            "System.Data.Common.DbCommand::ExecuteNonQueryAsync",
            "System.Data.Common.DbDataReader::DisposeAsync",
            "System.Data.Common.DbDataReader::GetString",
            "System.Data.Common.DbDataReader::IsDBNull",
            "System.Data.Common.DbDataReader::ReadAsync",
            "System.Data.Common.DbTransaction::CommitAsync",
            "System.Data.Common.DbTransaction::DisposeAsync"),
        ["Infrastructure.AI.RAG/Infrastructure.AI.RAG.DependencyInjection"] = new(
            "DI registration only: constructs the search clients and registers the RAG stores, which are declared on their own types.",
            "Azure.Search.Documents.KnowledgeBases.KnowledgeBaseRetrievalClient::.ctor",
            "Azure.Search.Documents.SearchClient::.ctor"),
        ["Infrastructure.AI/Infrastructure.AI.DependencyInjection"] = new(
            "DI registration only: constructs the workflow-state composite and the stores it fans out to, which are declared on their own types.",
            "calls channel Infrastructure.AI.StateManagement.CompositeStateManager"),
        ["Infrastructure.AI.KnowledgeGraph/Infrastructure.AI.KnowledgeGraph.DependencyInjection"] = new(
            "DI registration only: wraps the configured graph store in its compliance and tenant-isolation decorators; the stores are declared on their own types.",
            "calls channel Infrastructure.AI.KnowledgeGraph.Compliance.ComplianceAwareGraphStore",
            "calls channel Infrastructure.AI.KnowledgeGraph.Scoping.TenantIsolatedGraphStore"),
        ["Presentation.Common/Presentation.Common.Extensions.IServiceCollectionExtensions"] = new(
            "DI registration only: picks the distributed-cache backend (memory or Redis); the response cache it backs is declared where it is turned on, on AgentFactory.",
            "StackExchange.Redis.ConfigurationOptions::.ctor",
            "StackExchange.Redis.ConfigurationOptions::get_EndPoints",
            "StackExchange.Redis.ConfigurationOptions::set_ClientName",
            "StackExchange.Redis.ConfigurationOptions::set_Password",
            "StackExchange.Redis.ConfigurationOptions::set_ServiceName",
            "StackExchange.Redis.EndPointCollection::Add"),
        ["Presentation.Common/Presentation.Common.Extensions.HealthCheckServiceCollectionExtensions"] = new(
            "Health-check registration: probes dependency reachability; its in-memory history holds health status only.",
            "Azure.Storage.Blobs.BlobServiceClient::.ctor"),
        ["Presentation.AuditArchiver/Presentation.AuditArchiver.Program"] = new(
            "Composition root of the archiver job: builds the archive store and hands it to AuditArchiver; the archive is declared on the store types.",
            "Azure.Storage.Blobs.BlobContainerClient::.ctor"),
        ["Infrastructure.Common/Infrastructure.Common.Data.SqlConnectionFactory"] = new(
            "Client construction only: creates database connections from a provider factory and never reads or writes data itself.",
            "System.Data.Common.DbConnection::set_ConnectionString",
            "System.Data.Common.DbProviderFactory::CreateConnection"),
        ["Infrastructure.Observability/Infrastructure.Observability.Exporters.ObservabilityTelemetryConfigurator"] = new(
            "Telemetry pipeline configuration only: registers instrumentation, span processors and exporters, and holds no data.",
            "Npgsql.TracerProviderBuilderExtensions::AddNpgsql"),
        ["Presentation.Common/Presentation.Common.Helpers.ConfigurationHelper"] = new(
            "Developer config utility: copies the AppConfig section between appsettings files and holds no user data.",
            "System.IO.File::WriteAllText"),

        // Read-only access to storage.
        ["Infrastructure.AI.RAG/Infrastructure.AI.RAG.Retrieval.AzureKnowledgeBaseRetriever"] = new(
            "Read-only: its only SDK call queries a server-side knowledge base, and it writes nothing.",
            "Azure.Search.Documents.KnowledgeBases.KnowledgeBaseRetrievalClient::RetrieveAsync",
            "Azure.Search.Documents.KnowledgeBases.Models.KnowledgeBaseMessage::get_Content",
            "Azure.Search.Documents.KnowledgeBases.Models.KnowledgeBaseMessageTextContent::get_Text",
            "Azure.Search.Documents.KnowledgeBases.Models.KnowledgeBaseRetrievalRequest::.ctor",
            "Azure.Search.Documents.KnowledgeBases.Models.KnowledgeBaseRetrievalRequest::get_Intents",
            "Azure.Search.Documents.KnowledgeBases.Models.KnowledgeBaseRetrievalResponse::get_Response",
            "Azure.Search.Documents.KnowledgeBases.Models.KnowledgeRetrievalSemanticIntent::.ctor"),
        ["Infrastructure.AI.RAG/Infrastructure.AI.RAG.Retrieval.AzureSemanticReranker"] = new(
            "Read-only: it only searches an index to rerank results, and never uploads, merges or deletes documents.",
            "Azure.Search.Documents.Models.QueryCaption::.ctor",
            "Azure.Search.Documents.Models.QueryCaptionType::get_Extractive",
            "Azure.Search.Documents.Models.SearchDocument::get_Item",
            "Azure.Search.Documents.Models.SearchResult`1::get_Document",
            "Azure.Search.Documents.Models.SearchResult`1::get_Score",
            "Azure.Search.Documents.Models.SearchResult`1::get_SemanticSearch",
            "Azure.Search.Documents.Models.SearchResults`1::GetResultsAsync",
            "Azure.Search.Documents.Models.SemanticSearchOptions::.ctor",
            "Azure.Search.Documents.Models.SemanticSearchOptions::set_QueryCaption",
            "Azure.Search.Documents.Models.SemanticSearchOptions::set_SemanticConfigurationName",
            "Azure.Search.Documents.Models.SemanticSearchResult::get_RerankerScore",
            "Azure.Search.Documents.SearchClient::SearchAsync",
            "Azure.Search.Documents.SearchOptions::.ctor",
            "Azure.Search.Documents.SearchOptions::set_Filter",
            "Azure.Search.Documents.SearchOptions::set_QueryType",
            "Azure.Search.Documents.SearchOptions::set_SemanticSearch",
            "Azure.Search.Documents.SearchOptions::set_Size"),
        ["Infrastructure.AI.RAG/Infrastructure.AI.RAG.SqlDatabase.SafeSqlQueryExecutor"] = new(
            "Read-only query executor: rejects any non-SELECT or mutating SQL against a deployer-configured database and holds nothing itself.",
            "System.Data.Common.DbCommand::CreateParameter",
            "System.Data.Common.DbCommand::ExecuteReaderAsync",
            "System.Data.Common.DbCommand::get_Parameters",
            "System.Data.Common.DbCommand::set_CommandText",
            "System.Data.Common.DbCommand::set_CommandTimeout",
            "System.Data.Common.DbConnection::CreateCommand",
            "System.Data.Common.DbConnection::DisposeAsync",
            "System.Data.Common.DbConnection::OpenAsync",
            "System.Data.Common.DbDataReader::GetName",
            "System.Data.Common.DbDataReader::GetValue",
            "System.Data.Common.DbDataReader::IsDBNull",
            "System.Data.Common.DbDataReader::ReadAsync",
            "System.Data.Common.DbDataReader::get_FieldCount",
            "System.Data.Common.DbParameter::set_ParameterName",
            "System.Data.Common.DbParameter::set_Value",
            "System.Data.Common.DbParameterCollection::Add"),
        ["Infrastructure.AI/Infrastructure.AI.Tools.HardLinkInspector"] = new(
            "Read-only metadata probe: opens a file handle only to ask the OS for its hard-link count.",
            "System.IO.File::OpenHandle"),

        // Hashes, leases and counters that identify no one, and sweepers that only delete.
        ["Infrastructure.AI/Infrastructure.AI.Attestation.EfCoreAttestationStore"] = new(
            "Writes only attestation JSON (tool name, SHA-256 input and output hashes, HMAC signature, timestamps) onto planner rows that EfCorePlanStateStore owns.",
            "Microsoft.EntityFrameworkCore.DbContext::SaveChangesAsync",
            "Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions::AnyAsync",
            "Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions::AsNoTracking",
            "Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions::FirstOrDefaultAsync",
            "Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions::ToListAsync",
            "calls channel Infrastructure.AI.Persistence.PlannerDbContext"),
        ["Infrastructure.AI/Infrastructure.AI.Planner.PlannedTaskCanceller"] = new(
            "Read-only: finds the plan ids of a person's runs in progress so erasure can stop them (#907); writes nothing and keeps nothing.",
            "Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions::AsNoTracking",
            "Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions::ToListAsync",
            "calls channel Infrastructure.AI.Persistence.PlannerDbContext"),
        ["Infrastructure.AI/Infrastructure.AI.Audit.AuditPseudonymKey"] = new(
            "Writes the audit pseudonym key file (#903): 32 random bytes, base64, made once in a temporary file and moved into place. It holds the key the audit references are made with, not data about anyone.",
            "System.IO.File::Move",
            "System.IO.FileStream::.ctor"),
        ["Infrastructure.AI/Infrastructure.AI.Audit.UnreadableComplianceAuditRecords"] = new(
            "The compliance report's reader when a host replaced the trail with one that cannot be read back (#1000): every read fails with a fixed reason; it reads and keeps nothing.",
            "shares Application.Core.Compliance.IComplianceAuditRecords with store Infrastructure.AI.Audit.JsonlComplianceAuditTrail"),
        ["Infrastructure.AI/Infrastructure.AI.Audit.AuditChainCheckpointStore"] = new(
            "One small JSON file per audit chain holding only the last verified sequence number and hash.",
            "System.IO.File::Move",
            "System.IO.FileStream::.ctor"),
        ["Infrastructure.AI/Infrastructure.AI.Audit.AuditChainVerificationService"] = new(
            "Its verification receipts hold a chain name, validity, sequence numbers, the head hash and a failure reason; no audit record content is copied in.",
            "System.IO.FileStream::.ctor"),
        ["Infrastructure.AI/Infrastructure.AI.Audit.AuditArchiver"] = new(
            "The archiver job: copies finished audit day-files into an archive store and deletes the live copies; it keeps nothing itself.",
            "System.IO.FileStream::.ctor",
            "calls channel Infrastructure.AI.Audit.HashChainedJsonlWriter"),
        ["Infrastructure.AI/Infrastructure.AI.Conversations.SqliteConversationTurnLease"] = new(
            "Only stamps a lease token and expiry onto conversation rows that EfCoreConversationStore owns, and clears them on release.",
            "Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions::AsNoTracking",
            "Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions::ExecuteUpdateAsync",
            "Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions::ToListAsync",
            "calls channel Infrastructure.AI.Persistence.ConversationDbContext"),
        ["Infrastructure.AI/Infrastructure.AI.SkillTraining.SkillTrainingRunLease"] = new(
            "Lease row only (run id, random owner token, expiry), deleted on dispose; holds no personal data.",
            "Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions::ExecuteSqlInterpolatedAsync"),
        ["Infrastructure.AI/Infrastructure.AI.Documents.EfCoreDocumentLoadLedger"] = new(
            "When each document was last loaded (#901): a document id, which is a hash of where the document came from, and a time. Not who loaded it, the location, or the tenant; nothing that names a person. A row goes when its document is removed completely.",
            "Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions::AsNoTracking",
            "Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions::ExecuteDeleteAsync",
            "Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions::FirstOrDefaultAsync",
            "Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions::ToListAsync",
            "Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions::ExecuteSqlInterpolatedAsync",
            "calls channel Infrastructure.AI.Persistence.DocumentLoadDbContext"),
        ["Infrastructure.AI/Infrastructure.AI.Persistence.GovernanceStatePruner"] = new(
            "A retention sweeper that only deletes expired governance-state rows and holds nothing itself.",
            "Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions::ExecuteDeleteAsync",
            "calls channel Infrastructure.AI.Persistence.GovernanceStateDbContext"),

        // Null objects: the default when a durable store is switched off; they discard every write.
        ["Infrastructure.AI.RAG/Infrastructure.AI.RAG.Remote.RemoteCrossSessionMemoryStore"] = new(
            "Stand-in used when memory is hosted remotely: accepts and discards every write and recalls nothing; its owner purge throws so erasure reports Partial rather than claiming the remote copy is gone.",
            "shares Application.AI.Common.Interfaces.KnowledgeGraph.ICrossSessionMemoryStore with store Infrastructure.AI.RAG.GraphRag.CrossSessionMemoryStore"),
        ["Application.AI.Common/Application.AI.Common.Prompts.NullPromptUsageStore"] = new(
            "Null object used when prompt-usage tracking is off: discards every write and keeps nothing.",
            "shares Application.AI.Common.Prompts.Interfaces.IPromptUsageStore with store Infrastructure.AI.Prompts.Persistence.EfCorePromptUsageStore"),
        ["Infrastructure.AI/Infrastructure.AI.Escalation.NullEscalationStateStore"] = new(
            "Null object used when durable escalations are off: discards every write and keeps nothing.",
            "shares Application.AI.Common.Interfaces.Escalation.IEscalationStateStore with store Infrastructure.AI.Escalation.EfCoreEscalationStateStore"),
        ["Infrastructure.AI/Infrastructure.AI.Governance.NullToolCallLedger"] = new(
            "Null object used when the durable call-once ledger is off: discards every write and keeps nothing.",
            "shares Application.AI.Common.Interfaces.Governance.IToolCallLedger with store Infrastructure.AI.Governance.EfCoreToolCallLedger"),
        ["Infrastructure.Observability/Infrastructure.Observability.Persistence.NullObservabilityStore"] = new(
            "Null object used when no observability database is configured: discards every write and keeps nothing.",
            "shares Application.AI.Common.Interfaces.IObservabilityStore with store Infrastructure.Observability.Persistence.PostgresObservabilityStore"),

        // Release adapters (#991): the evaluation submission store also listens for reclaimed runs, so the scan pairs it
        // with the other listeners, which hold nothing and only forward a run's release to the holder they wrap.
        ["Infrastructure.AI/Infrastructure.AI.Runs.EvalSubmissionReclaimListener"] = new(
            "Forwards a reclaimed run's id to the evaluation submission store so it releases that run's submission; holds nothing itself.",
            "shares Application.AI.Common.Interfaces.Runs.IRunReclaimListener with store Infrastructure.AI.Runs.InMemoryEvalRunSubmissionStore"),
        ["Infrastructure.AI/Infrastructure.AI.Runs.RunProgressReclaimListener"] = new(
            "Forwards a reclaimed run's id to the run progress broker so it forgets that run's progress; holds nothing itself.",
            "shares Application.AI.Common.Interfaces.Runs.IRunReclaimListener with store Infrastructure.AI.Runs.InMemoryEvalRunSubmissionStore"),

        // Scratch space removed when its run ends.
        ["Infrastructure.AI/Infrastructure.AI.Sandbox.DockerSandboxExecutor"] = new(
            "Sandbox scratch space: writes input.json into a per-run workspace that is deleted with the container when the run ends.",
            "System.IO.File::WriteAllTextAsync"),
        ["Infrastructure.AI/Infrastructure.AI.Sandbox.SandboxWorkspace"] = new(
            "Sandbox workspace lifecycle: prepares scratch directories, copies seed content into them and deletes them; nothing outlives a sandbox run.",
            "System.IO.File::Copy"),
        ["Infrastructure.AI/Infrastructure.AI.Bundles.BundleStagingService"] = new(
            "Extracts uploaded agent bundles (agent, skill and plugin definitions) into staging directories deleted on removal, expiry, shutdown or failure; scratch space for definitions, not user data.",
            "System.IO.FileStream::.ctor"),
        ["Infrastructure.AI/Infrastructure.AI.SkillTraining.ProposerEvidenceTree"] = new(
            "Temporary files: one training step's evidence in an owner-only folder that AgenticPatchProposer deletes when the step ends.",
            "System.IO.File::WriteAllTextAsync"),
        ["Infrastructure.AI/Infrastructure.AI.MetaHarness.AgentEvaluationService"] = new(
            "Writes candidate skill files into a temp folder per run, deleted when the run's tasks finish or fail; eval traces go through the trace store.",
            "System.IO.File::WriteAllText"),

        // Deployer-authored inputs and harness configuration, not user data.
        ["Infrastructure.AI/Infrastructure.AI.Tools.FileSystemService"] = new(
            "The agent's file-system tool: reads and writes files only inside deployer-chosen allowed paths and has no storage of its own.",
            "System.IO.File::WriteAllTextAsync"),
        ["Application.Core/Application.Core.CQRS.MetaHarness.RunHarnessOptimizationCommandHandler"] = new(
            "Writes harness-optimizer artifacts (manifests, scores, proposer notes, skill and prompt snapshots) derived from deployer-authored eval tasks, not user conversations; run folders are pruned to MaxRunsToKeep.",
            "System.IO.File::AppendAllTextAsync",
            "System.IO.File::Move",
            "System.IO.File::WriteAllText",
            "System.IO.File::WriteAllTextAsync"),
        ["Infrastructure.AI/Infrastructure.AI.MetaHarness.FileSystemHarnessCandidateRepository"] = new(
            "Optimizer candidates: skill-file, system-prompt and config snapshots with scores; harness configuration, not user data, pruned to MaxRunsToKeep.",
            "System.IO.File::Move",
            "System.IO.File::WriteAllLinesAsync",
            "System.IO.File::WriteAllTextAsync"),
        ["Infrastructure.AI/Infrastructure.AI.MetaHarness.FileSystemRegressionSuiteService"] = new(
            "regression_suite.json holds only eval task ids, a pass threshold and a timestamp.",
            "System.IO.File::Move",
            "System.IO.File::WriteAllTextAsync"),
        ["Infrastructure.AI.Evaluation/Infrastructure.AI.Evaluation.Persistence.EfCoreEvalRunStore"] = new(
            "Eval-run reports (case inputs, expected and model outputs, scores) whose cases come only from deployer-authored YAML datasets, not user conversations.",
            "Microsoft.Data.Sqlite.SqliteException::get_SqliteErrorCode",
            "Microsoft.EntityFrameworkCore.DbContext::SaveChangesAsync",
            "Microsoft.EntityFrameworkCore.DbSet`1::Add",
            "Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions::AnyAsync",
            "Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions::AsNoTracking",
            "Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions::FirstOrDefaultAsync",
            "Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions::ToListAsync",
            "calls channel Infrastructure.AI.Evaluation.Persistence.EvalDashboardDbContext"),
        ["Infrastructure.AI/Infrastructure.AI.SkillTraining.EfSkillTrainingCheckpointStore"] = new(
            "Skill-training checkpoints (skill text, scores, edits, lessons) from rollouts over operator-supplied training items, keyed by run id with no user or conversation identifier. A deployer training on real user data must treat this database as a store.",
            "Microsoft.EntityFrameworkCore.DbContext::SaveChangesAsync",
            "Microsoft.EntityFrameworkCore.DbSet`1::Add",
            "Microsoft.EntityFrameworkCore.DbSet`1::FindAsync",
            "Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions::AsNoTracking",
            "Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions::FirstOrDefaultAsync",
            "Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions::ToListAsync",
            "Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions::ExecuteSqlInterpolatedAsync",
            "calls channel Infrastructure.AI.Persistence.SkillTrainingDbContext"),
        ["Presentation.EvalRunner/Presentation.EvalRunner.HarmonicWriteEval.HarmonicWriteEvalCli"] = new(
            "Eval CLI output: writes a metrics report computed from a fixed fixture to an operator-chosen path.",
            "System.IO.File::WriteAllTextAsync"),
        ["Presentation.EvalRunner/Presentation.EvalRunner.Program"] = new(
            "Eval CLI output: writes evaluation reports over deployer-supplied datasets to operator-chosen paths and keeps no store of its own.",
            "System.IO.File::Create"),
        ["Application.Common/Application.Common.MediatRBehaviors.CachingBehavior`2"] = new(
            "Caches only queries that implement ICacheableQuery, and no production query does today; one that opts in later is cached for 5 minutes by default.",
            "Microsoft.Extensions.Caching.Hybrid.HybridCache::GetOrCreateAsync"),
    };
}
