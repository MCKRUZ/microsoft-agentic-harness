using Xunit;
using System.Text.RegularExpressions;
using Application.AI.Common.CQRS.SkillTraining.TrainSkill;
using Application.AI.Common.Evaluation.Interfaces;
using Application.AI.Common.Evaluation.Models;
using Application.AI.Common.Extensions;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Application.AI.Common.Interfaces.SkillTraining;
using Application.AI.Common.Interfaces.Tools;
using Application.AI.Common.Services.ClaimVerification;
using Application.AI.Common.Services.SkillTraining;
using Application.Common.Interfaces.Common;
using Application.Core.CQRS.Agents.RunConversation;
using Domain.AI.Evaluation;
using Domain.AI.KnowledgeGraph.Models;
using Domain.AI.SkillTraining;
using Domain.Common;
using Domain.Common.Config;
using Domain.Common.Config.AI.SkillTraining;
using FluentAssertions;
using Infrastructure.AI.Evaluation.Metrics;
using Infrastructure.AI.Evaluation.Loaders;
using Infrastructure.AI.Evaluation.SkillTraining;
using Infrastructure.AI.SkillTraining;
using Infrastructure.AI.Tools;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Tests.Common;

namespace Infrastructure.AI.Evaluation.Tests.SkillTraining;

/// <summary>
/// The whole skill-training stack, composed from its real parts and run with no credentials (#846).
/// </summary>
/// <remarks>
/// <para>
/// Real: the training handler dispatched through MediatR, the patch applier, the editable-surface fence,
/// the accept gate, the grader-marker tripwires, the agentic proposer with its evidence writer and
/// confined evidence tool, the governed rollout runner reading a real YAML dataset and scoring with a real
/// metric, the experience distiller and library, and the checkpoint store.
/// </para>
/// <para>
/// Faked at the two edges that need a model: the agent session the proposer talks to (a scripted handler
/// for the one MediatR request it sends, which reads the evidence the way a model would, through the real
/// tool) and the agent turn each rollout runs (an invoker that does the task well only if the candidate
/// skill tells it to). Memory is an in-process dictionary.
/// </para>
/// <para>
/// Each test pins what happens when one wired control is missing: take the control out and the test for it
/// fails, which is the evidence that the composition really has it.
/// </para>
/// </remarks>
public sealed partial class SkillTrainingOfflineDemoTests : IDisposable
{
    private const string Rule = "- state the total";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"skill-training-offline-{Guid.NewGuid():N}");
    private readonly string _evidenceRoot;
    private readonly string _datasetPath;

    public SkillTrainingOfflineDemoTests()
    {
        _evidenceRoot = Path.Combine(_root, "evidence");
        _datasetPath = Path.Combine(_root, "invoices.yaml");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static string Dataset(string extraCaseOverrides = "") => $"""
        name: invoices
        version: 1.0.0
        cases:
          - id: t1
            input: "Reconcile invoice 1"
            tags: ["split:train", "invoices"]
            {extraCaseOverrides}
            metrics:
              - key: contains_all
                threshold: 1.0
                parameters:
                  values: "TOTAL:"
          - id: t2
            input: "Reconcile invoice 2"
            tags: ["split:train", "invoices"]
            metrics:
              - key: contains_all
                threshold: 1.0
                parameters:
                  values: "TOTAL:"
          - id: v1
            input: "Reconcile invoice 3"
            tags: ["split:val", "invoices"]
            metrics:
              - key: contains_all
                threshold: 1.0
                parameters:
                  values: "TOTAL:"
          - id: v2
            input: "Reconcile invoice 4"
            tags: ["split:val", "invoices"]
            metrics:
              - key: contains_all
                threshold: 1.0
                parameters:
                  values: "TOTAL:"
        """;

    private static TrainSkillConfig Config(int epochs = 2) => new()
    {
        Epochs = epochs, StepsPerEpoch = 1, LrStart = 4, LrMin = 1, LrScheduler = "constant", Patience = 5,
        GateMetric = GateMetric.Hard, UseSlowUpdate = false, UseMetaSkill = true, TrainBatchSize = 2, ValBatchSize = 2, Seed = 1
    };

    private static TrainSkillCommand Command(TrainSkillConfig config) => new()
    {
        RunId = "run-1",
        SkillId = "invoice-skill",
        Domain = "invoices",
        InitialSkill = "# Invoice skill\n- be brief\n",
        Config = config
    };

    private Rig NewRig(Func<Session, Task<string>> agentSession, Func<string, string>? agentOutput = null)
    {
        File.WriteAllText(_datasetPath, Dataset());
        return new Rig(_evidenceRoot, _datasetPath, agentSession, agentOutput);
    }

    /// <summary>What the scripted agent session can do: read the evidence the way a model would, through the real tool.</summary>
    private sealed class Session(string mission, IServiceProvider services)
    {
        private static readonly Regex Folder = new(@"(?<dir>\S+?)/skill/SKILL\.md", RegexOptions.Compiled);

        public string Mission => mission;

        /// <summary>This step's evidence folder, as the mission names it.</summary>
        public string Dir => Folder.Match(mission).Groups["dir"].Value;

        public async Task<string> ReadAsync(string path) => await CallAsync("read", path);

        public async Task<string> ListAsync(string path) => await CallAsync("list", path);

        private async Task<string> CallAsync(string operation, string path)
        {
            var tool = services.GetRequiredKeyedService<ITool>(EvidenceReaderTool.ToolName);
            var result = await tool.ExecuteAsync(operation, new Dictionary<string, object?> { ["path"] = path });
            return result.Success ? result.Output : "ERROR: " + result.Output;
        }

        /// <summary>The skill document the proposer was shown for this step.</summary>
        public async Task<string> SkillAsync() => await ReadAsync(Dir + "/skill/SKILL.md");

        /// <summary>A reply in the format the shipped skill-optimizer asks for.</summary>
        public static string Reply(string document) =>
            $"===BEGIN SKILL===\n{document}\n===END SKILL===\n===BEGIN NOTES===\nrun 1: no total in the answer\n===END NOTES===";

        public const string KeepBase = "KEEP_BASE";
    }

    /// <summary>The composed stack and the faked edges, built the way a host's container would be.</summary>
    private sealed class Rig : IDisposable
    {
        private readonly ServiceProvider _provider;

        public Rig(string evidenceRoot, string datasetPath, Func<Session, Task<string>> agentSession, Func<string, string>? agentOutput)
        {
            Memory = new FakeMemory();
            Handler = new AgentSessionHandler(agentSession, () => _provider!);
            Invoker = new RuleFollowingInvoker(agentOutput);

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(TimeProvider.System);
            services.AddSingleton<IOwnerOnlyDirectoryCreator, PlainDirectoryCreator>();
            services.AddSingleton<IKnowledgeMemory>(Memory);
            services.AddSingleton(new ClaimVerificationRunner(
                new RuleBasedClaimConsequenceClassifier(),
                new NotConfiguredClaimVerifier(NullLogger<NotConfiguredClaimVerifier>.Instance),
                new ServiceCollection().BuildServiceProvider(),
                new StaticMonitor<AppConfig>(new AppConfig()),
                NullLogger<ClaimVerificationRunner>.Instance));
            services.AddSingleton<IOptionsMonitor<SkillTrainingConfig>>(new StaticMonitor<SkillTrainingConfig>(new SkillTrainingConfig
            {
                DatasetPath = datasetPath,
                RolloutAgentName = "worker",
                EvidenceRoot = evidenceRoot,
                MaxRolloutConcurrency = 1
            }));

            // Only the skill-training handlers: the rest of the assembly's handlers need services this
            // composition has no reason to provide.
            services.AddMediatR(c =>
            {
                c.RegisterServicesFromAssembly(typeof(TrainSkillCommand).Assembly);
                c.TypeEvaluator = t => t.Namespace?.StartsWith("Application.AI.Common.CQRS.SkillTraining", StringComparison.Ordinal) == true;
            });
            services.AddTransient<IRequestHandler<RunConversationCommand, ConversationResult>>(_ => Handler);

            // The training subsystem's own composition, with its fail-fast defaults...
            services.AddSkillTrainingDependencies();

            // ...replaced the way the infrastructure projects replace them.
            services.AddSingleton(new SkillTrainingEvidenceRoot(evidenceRoot));
            services.AddKeyedSingleton<ITool>(EvidenceReaderTool.ToolName, (sp, _) =>
            {
                Directory.CreateDirectory(evidenceRoot);
                return new EvidenceReaderTool(new FileSystemService(NullLogger<FileSystemService>.Instance, [evidenceRoot]));
            });
            services.ReplacePlaceholder<IPatchProposer, NotConfiguredPatchProposer, AgenticPatchProposer>(ServiceLifetime.Scoped);
            services.AddEvalMetric<ContainsAllMetric>("contains_all");
            services.AddSingleton<IEvalDatasetLoader, YamlEvalDatasetLoader>();
            services.AddSingleton<IAgentInvoker>(Invoker);
            services.ReplacePlaceholder<IRolloutRunner, NotConfiguredRolloutRunner, GovernedRolloutRunner>(ServiceLifetime.Scoped);

            _provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        }

        public FakeMemory Memory { get; }
        public AgentSessionHandler Handler { get; }
        public RuleFollowingInvoker Invoker { get; }

        public async Task<Result<SkillTrainingRunResult>> TrainAsync(TrainSkillCommand command)
        {
            await using var scope = _provider.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IMediator>().Send(command);
        }

        public IReadOnlyList<SkillTrainingCheckpoint> Checkpoints()
        {
            var store = _provider.GetRequiredService<ISkillTrainingCheckpointStore>();
            return store.ListAsync("run-1", CancellationToken.None).GetAwaiter().GetResult().OrderBy(c => c.Step).ToList();
        }

        public void Dispose() => _provider.Dispose();
    }

    /// <summary>Stands in for the agent session behind <c>RunConversationCommand</c>.</summary>
    private sealed class AgentSessionHandler(Func<Session, Task<string>> script, Func<IServiceProvider> services)
        : IRequestHandler<RunConversationCommand, ConversationResult>
    {
        public int Calls { get; private set; }

        public async Task<ConversationResult> Handle(RunConversationCommand request, CancellationToken cancellationToken)
        {
            Calls++;
            var reply = await script(new Session(request.UserMessages.Single(), services()));
            return new ConversationResult { Success = true, Turns = [], FinalResponse = reply };
        }
    }

    /// <summary>
    /// An agent that gives a total only when the candidate skill (arriving as the system prompt) tells it
    /// to, which is the whole task the loop has to teach it.
    /// </summary>
    private sealed class RuleFollowingInvoker(Func<string, string>? output) : IAgentInvoker
    {
        public List<IReadOnlyDictionary<string, string>> RunLevel { get; } = [];

        public Task<AgentInvocationResult> InvokeAsync(
            EvalCase @case, IReadOnlyDictionary<string, string>? runLevelOverrides, bool forceDeterministic, CancellationToken cancellationToken)
        {
            lock (RunLevel)
                RunLevel.Add(runLevelOverrides ?? new Dictionary<string, string>());

            var prompt = runLevelOverrides?["system_prompt"] ?? string.Empty;
            var answer = output?.Invoke(@case.Id) ?? (prompt.Contains(Rule, StringComparison.Ordinal) ? "TOTAL: 90" : "all reconciled");
            return Task.FromResult(new AgentInvocationResult { Success = true, Output = answer });
        }
    }

    private sealed class FakeMemory : IKnowledgeMemory
    {
        private readonly List<GraphNode> _nodes = [];

        public IReadOnlyList<GraphNode> Nodes
        {
            get
            {
                lock (_nodes)
                    return [.. _nodes];
            }
        }

        public Task<MemoryWriteDecision> RememberAsync(string key, string content, string entityType = "Fact", CancellationToken cancellationToken = default)
        {
            lock (_nodes)
            {
                _nodes.RemoveAll(n => n.Name == key);
                _nodes.Add(new GraphNode
                {
                    Id = "memory:" + key,
                    Name = key,
                    Type = entityType,
                    Properties = new Dictionary<string, string> { [GraphNodeMemoryExtensions.ContentPropertyKey] = content }
                });
            }

            return Task.FromResult(MemoryWriteDecision.Allow());
        }

        public Task<IReadOnlyList<GraphNode>> RecallAsync(string query, int maxResults = 5, string? entityType = null, CancellationToken cancellationToken = default)
        {
            lock (_nodes)
                return Task.FromResult<IReadOnlyList<GraphNode>>(
                    [.. _nodes.Where(n => n.Name.StartsWith(query, StringComparison.Ordinal) && (entityType is null || n.Type == entityType)).Take(maxResults)]);
        }

        public Task ForgetAsync(string key, CancellationToken cancellationToken = default)
        {
            lock (_nodes)
                _nodes.RemoveAll(n => n.Name == key);
            return Task.CompletedTask;
        }

        public Task ImproveAsync(string userMessage, string assistantResponse, IReadOnlyList<string> relevantNodeIds, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class StaticMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
