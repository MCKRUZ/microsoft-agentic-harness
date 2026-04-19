using Application.AI.Common.Factories;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Context;
using Application.AI.Common.Interfaces.Skills;
using Application.AI.Common.Interfaces.Tools;
using Application.AI.Common.Interfaces.Traces;
using Domain.AI.Skills;
using Domain.AI.Tools;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Domain.Common.MetaHarness;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.Factories;

/// <summary>
/// Tests for <see cref="AgentExecutionContextFactory"/> covering framework type resolution,
/// deployment name resolution, instruction building, tool provisioning, agent naming,
/// additional properties, middleware types, trace scope wiring, and budget tracking.
/// </summary>
public class AgentExecutionContextFactoryTests
{
    private static AgentExecutionContextFactory CreateFactory(
        AIAgentFrameworkClientType configuredClientType = AIAgentFrameworkClientType.AzureOpenAI,
        string? deployment = "default-model",
        IExecutionTraceStore? traceStore = null,
        IContextBudgetTracker? budgetTracker = null,
        IMcpToolProvider? mcpToolProvider = null,
        IToolConverter? toolConverter = null,
        IServiceProvider? serviceProvider = null,
        ISkillContentProvider? skillContentProvider = null)
    {
        var appConfig = new AppConfig
        {
            AI = new AIConfig
            {
                AgentFramework = new AgentFrameworkConfig
                {
                    ClientType = configuredClientType,
                    DefaultDeployment = deployment,
                    ApiKey = "test-key",
                    Endpoint = "https://test.example.com"
                }
            }
        };
        var monitor = Mock.Of<IOptionsMonitor<AppConfig>>(m => m.CurrentValue == appConfig);
        var services = serviceProvider ?? new ServiceCollection().BuildServiceProvider();

        return new AgentExecutionContextFactory(
            NullLogger<AgentExecutionContextFactory>.Instance,
            monitor,
            services,
            NullLoggerFactory.Instance,
            toolConverter: toolConverter,
            mcpToolProvider: mcpToolProvider,
            budgetTracker: budgetTracker,
            traceStore: traceStore,
            skillContentProvider: skillContentProvider);
    }

    private static SkillDefinition SimpleSkill(string id = "test-skill", string? name = null) => new()
    {
        Id = id,
        Name = name ?? id,
        Instructions = "You are a test agent."
    };

    // --- Framework type resolution ---

    [Fact]
    public async Task MapToAgentContext_NoFrameworkTypeInOptions_UsesConfiguredClientType()
    {
        var factory = CreateFactory(AIAgentFrameworkClientType.AzureAIInference);
        var options = new SkillAgentOptions();

        var context = await factory.MapToAgentContextAsync(SimpleSkill(), options);

        context.AIAgentFrameworkType.Should().Be(AIAgentFrameworkClientType.AzureAIInference);
    }

    [Fact]
    public async Task MapToAgentContext_FrameworkTypeInOptions_OverridesConfig()
    {
        var factory = CreateFactory(AIAgentFrameworkClientType.AzureAIInference);
        var options = new SkillAgentOptions
        {
            FrameworkType = AIAgentFrameworkClientType.OpenAI
        };

        var context = await factory.MapToAgentContextAsync(SimpleSkill(), options);

        context.AIAgentFrameworkType.Should().Be(AIAgentFrameworkClientType.OpenAI);
    }

    [Fact]
    public async Task MapToAgentContext_NoFrameworkTypeAnywhere_DefaultsToAzureOpenAI()
    {
        var appConfig = new AppConfig { AI = new AIConfig { AgentFramework = new AgentFrameworkConfig() } };
        var monitor = Mock.Of<IOptionsMonitor<AppConfig>>(m => m.CurrentValue == appConfig);
        var factory = new AgentExecutionContextFactory(
            NullLogger<AgentExecutionContextFactory>.Instance,
            monitor,
            new ServiceCollection().BuildServiceProvider(),
            NullLoggerFactory.Instance);

        var context = await factory.MapToAgentContextAsync(SimpleSkill(), new SkillAgentOptions());

        context.AIAgentFrameworkType.Should().Be(AIAgentFrameworkClientType.AzureOpenAI);
    }

    // --- Deployment name resolution ---

    [Fact]
    public async Task MapToAgentContext_OptionsDeploymentName_TakesPriority()
    {
        var factory = CreateFactory(deployment: "config-model");
        var options = new SkillAgentOptions { DeploymentName = "options-model" };

        var context = await factory.MapToAgentContextAsync(SimpleSkill(), options);

        context.DeploymentName.Should().Be("options-model");
    }

    [Fact]
    public async Task MapToAgentContext_SkillModelOverride_TakesPriorityOverConfig()
    {
        var factory = CreateFactory(deployment: "config-model");
        var skill = SimpleSkill();
        skill.ModelOverride = "skill-model";

        var context = await factory.MapToAgentContextAsync(skill, new SkillAgentOptions());

        context.DeploymentName.Should().Be("skill-model");
    }

    [Fact]
    public async Task MapToAgentContext_SkillMetadataDeployment_UsedWhenNoOverride()
    {
        var factory = CreateFactory(deployment: "config-model");
        var skill = SimpleSkill();
        skill.Metadata = new Dictionary<string, object> { ["deployment"] = "metadata-model" };

        var context = await factory.MapToAgentContextAsync(skill, new SkillAgentOptions());

        context.DeploymentName.Should().Be("metadata-model");
    }

    [Fact]
    public async Task MapToAgentContext_NoDeploymentAnywhere_UsesConfigDefault()
    {
        var factory = CreateFactory(deployment: "config-model");

        var context = await factory.MapToAgentContextAsync(SimpleSkill(), new SkillAgentOptions());

        context.DeploymentName.Should().Be("config-model");
    }

    // --- Instruction building ---

    [Fact]
    public async Task MapToAgentContext_InstructionsOnly_SetsInstruction()
    {
        var factory = CreateFactory();
        var skill = SimpleSkill();
        skill.Instructions = "Do the thing.";

        var context = await factory.MapToAgentContextAsync(skill, new SkillAgentOptions());

        context.Instruction.Should().Be("Do the thing.");
    }

    [Fact]
    public async Task MapToAgentContext_InstructionsAndAdditionalContext_JoinsWithDoubleNewline()
    {
        var factory = CreateFactory();
        var skill = SimpleSkill();
        skill.Instructions = "Base instructions.";
        var options = new SkillAgentOptions { AdditionalContext = "Extra context." };

        var context = await factory.MapToAgentContextAsync(skill, options);

        context.Instruction.Should().Be("Base instructions.\n\nExtra context.");
    }

    // --- Agent naming ---

    [Fact]
    public async Task MapToAgentContext_AgentNameOverride_UsesOverride()
    {
        var factory = CreateFactory();
        var options = new SkillAgentOptions { AgentNameOverride = "CustomAgent" };

        var context = await factory.MapToAgentContextAsync(SimpleSkill(), options);

        context.Name.Should().Be("CustomAgent");
    }

    [Fact]
    public async Task MapToAgentContext_HyphenatedSkillName_ConvertsToPascalCaseWithAgentSuffix()
    {
        var factory = CreateFactory();
        var skill = SimpleSkill(name: "research-assistant");

        var context = await factory.MapToAgentContextAsync(skill, new SkillAgentOptions());

        context.Name.Should().Be("ResearchAssistantAgent");
    }

    [Fact]
    public async Task MapToAgentContext_SkillNameAlreadyEndsWithAgent_DoesNotDuplicate()
    {
        var factory = CreateFactory();
        var skill = SimpleSkill(name: "research-agent");

        var context = await factory.MapToAgentContextAsync(skill, new SkillAgentOptions());

        context.Name.Should().Be("ResearchAgent");
        context.Name.Should().NotEndWith("AgentAgent");
    }

    [Fact]
    public async Task MapToAgentContext_UnderscoreSeparatedName_ConvertsToPascalCase()
    {
        var factory = CreateFactory();
        var skill = SimpleSkill(name: "code_reviewer");

        var context = await factory.MapToAgentContextAsync(skill, new SkillAgentOptions());

        context.Name.Should().Be("CodeReviewerAgent");
    }

    // --- Additional properties ---

    [Fact]
    public async Task MapToAgentContext_SetsSkillIdAndNameInAdditionalProperties()
    {
        var factory = CreateFactory();
        var skill = SimpleSkill(id: "agents/research");
        skill.Name = "research-skill";

        var context = await factory.MapToAgentContextAsync(skill, new SkillAgentOptions());

        context.AdditionalProperties.Should().ContainKey("skillId")
            .WhoseValue.Should().Be("agents/research");
        context.AdditionalProperties.Should().ContainKey("skillName")
            .WhoseValue.Should().Be("research-skill");
        context.AdditionalProperties.Should().ContainKey("loadedAt");
    }

    [Fact]
    public async Task MapToAgentContext_SkillWithCategory_IncludesCategoryInAdditionalProperties()
    {
        var factory = CreateFactory();
        var skill = SimpleSkill();
        skill.Category = "analysis";

        var context = await factory.MapToAgentContextAsync(skill, new SkillAgentOptions());

        context.AdditionalProperties.Should().ContainKey("category")
            .WhoseValue.Should().Be("analysis");
    }

    [Fact]
    public async Task MapToAgentContext_SkillWithTags_IncludesTagsInAdditionalProperties()
    {
        var factory = CreateFactory();
        var skill = SimpleSkill();
        skill.Tags = new List<string> { "research", "ai" };

        var context = await factory.MapToAgentContextAsync(skill, new SkillAgentOptions());

        context.AdditionalProperties.Should().ContainKey("tags");
    }

    [Fact]
    public async Task MapToAgentContext_SkillWithVersion_IncludesVersionInAdditionalProperties()
    {
        var factory = CreateFactory();
        var skill = SimpleSkill();
        skill.Version = "2.0";

        var context = await factory.MapToAgentContextAsync(skill, new SkillAgentOptions());

        context.AdditionalProperties.Should().ContainKey("version")
            .WhoseValue.Should().Be("2.0");
    }

    [Fact]
    public async Task MapToAgentContext_SkillWithMetadata_PrefixedWithSkill()
    {
        var factory = CreateFactory();
        var skill = SimpleSkill();
        skill.Metadata = new Dictionary<string, object> { ["priority"] = "high" };

        var context = await factory.MapToAgentContextAsync(skill, new SkillAgentOptions());

        context.AdditionalProperties.Should().ContainKey("skill_priority")
            .WhoseValue.Should().Be("high");
    }

    [Fact]
    public async Task MapToAgentContext_OptionsAdditionalProperties_MergedIntoContext()
    {
        var factory = CreateFactory();
        var options = new SkillAgentOptions
        {
            AdditionalProperties = new Dictionary<string, object> { ["custom"] = "value" }
        };

        var context = await factory.MapToAgentContextAsync(SimpleSkill(), options);

        context.AdditionalProperties.Should().ContainKey("custom")
            .WhoseValue.Should().Be("value");
    }

    // --- Middleware types ---

    [Fact]
    public async Task MapToAgentContext_AlwaysIncludesDefaultMiddleware()
    {
        var factory = CreateFactory();

        var context = await factory.MapToAgentContextAsync(SimpleSkill(), new SkillAgentOptions());

        context.MiddlewareTypes.Should().NotBeNull();
        context.MiddlewareTypes.Should().Contain(typeof(Application.AI.Common.Middleware.ObservabilityMiddleware));
        context.MiddlewareTypes.Should().Contain(typeof(Application.AI.Common.Middleware.ToolDiagnosticsMiddleware));
    }

    [Fact]
    public async Task MapToAgentContext_OptionsMiddleware_AppendedToDefaults()
    {
        var factory = CreateFactory();
        var options = new SkillAgentOptions
        {
            MiddlewareTypes = [typeof(string)] // Just a type placeholder for testing
        };

        var context = await factory.MapToAgentContextAsync(SimpleSkill(), options);

        context.MiddlewareTypes.Should().HaveCountGreaterThan(2);
        context.MiddlewareTypes.Should().Contain(typeof(string));
    }

    // --- Budget tracking ---

    [Fact]
    public async Task MapToAgentContext_WithBudgetTracker_RecordsSystemPromptAllocation()
    {
        var budgetTracker = new Mock<IContextBudgetTracker>();
        var factory = CreateFactory(budgetTracker: budgetTracker.Object);

        await factory.MapToAgentContextAsync(SimpleSkill(), new SkillAgentOptions());

        budgetTracker.Verify(
            b => b.RecordAllocation(It.IsAny<string>(), "system_prompt", It.Is<int>(i => i > 0)),
            Times.Once);
    }

    // --- Tool provisioning ---

    [Fact]
    public async Task MapToAgentContext_SkillWithPreCreatedTools_IncludesInTools()
    {
        var factory = CreateFactory();
        var mockTool = AIFunctionFactory.Create(() => "ok", "my_tool");
        var skill = SimpleSkill();
        skill.Tools = new List<AITool> { mockTool };

        var context = await factory.MapToAgentContextAsync(skill, new SkillAgentOptions());

        context.Tools.Should().Contain(t => t.Name == "my_tool");
    }

    [Fact]
    public async Task MapToAgentContext_OptionsAdditionalTools_IncludesInTools()
    {
        var factory = CreateFactory();
        var extraTool = AIFunctionFactory.Create(() => "extra", "extra_tool");
        var options = new SkillAgentOptions
        {
            AdditionalTools = new List<AITool> { extraTool }
        };

        var context = await factory.MapToAgentContextAsync(SimpleSkill(), options);

        context.Tools.Should().Contain(t => t.Name == "extra_tool");
    }

    [Fact]
    public async Task MapToAgentContext_DuplicateToolNames_DeduplicatedByName()
    {
        var factory = CreateFactory();
        var tool1 = AIFunctionFactory.Create(() => "a", "shared_tool");
        var tool2 = AIFunctionFactory.Create(() => "b", "shared_tool");
        var skill = SimpleSkill();
        skill.Tools = new List<AITool> { tool1 };
        var options = new SkillAgentOptions
        {
            AdditionalTools = new List<AITool> { tool2 }
        };

        var context = await factory.MapToAgentContextAsync(skill, options);

        context.Tools.Should().ContainSingle(t => t.Name == "shared_tool");
    }

    [Fact]
    public async Task MapToAgentContext_ToolDeclaration_TriesMcpFirst()
    {
        var mcpTools = new List<AITool> { AIFunctionFactory.Create(() => "mcp", "search") };
        var mcpProvider = new Mock<IMcpToolProvider>();
        mcpProvider
            .Setup(p => p.GetToolsAsync("search", It.IsAny<CancellationToken>()))
            .ReturnsAsync(mcpTools);

        var factory = CreateFactory(mcpToolProvider: mcpProvider.Object);
        var skill = SimpleSkill();
        skill.ToolDeclarations = new List<ToolDeclaration>
        {
            new() { Name = "search" }
        };

        var context = await factory.MapToAgentContextAsync(skill, new SkillAgentOptions());

        context.Tools.Should().Contain(t => t.Name == "search");
    }

    [Fact]
    public async Task MapToAgentContext_ToolDeclaration_FallsBackToKeyedDI()
    {
        // MCP returns empty, keyed DI has the tool
        var mcpProvider = new Mock<IMcpToolProvider>();
        mcpProvider
            .Setup(p => p.GetToolsAsync("calc", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AITool>());

        var toolMock = new Mock<ITool>();
        toolMock.Setup(t => t.Name).Returns("calc");
        toolMock.Setup(t => t.Description).Returns("Calculator");
        toolMock.Setup(t => t.SupportedOperations).Returns(["add"]);

        var convertedTool = AIFunctionFactory.Create(() => "converted", "calc");
        var converter = new Mock<IToolConverter>();
        converter.Setup(c => c.Convert(toolMock.Object, null)).Returns(convertedTool);

        var services = new ServiceCollection();
        services.AddKeyedSingleton<ITool>("calc", toolMock.Object);
        var sp = services.BuildServiceProvider();

        var factory = CreateFactory(
            mcpToolProvider: mcpProvider.Object,
            toolConverter: converter.Object,
            serviceProvider: sp);

        var skill = SimpleSkill();
        skill.ToolDeclarations = new List<ToolDeclaration>
        {
            new() { Name = "calc" }
        };

        var context = await factory.MapToAgentContextAsync(skill, new SkillAgentOptions());

        context.Tools.Should().Contain(t => t.Name == "calc");
    }

    // --- Trace scope wiring ---

    [Fact]
    public async Task CreateContext_WithoutTraceScope_SetsForExecutionScopeOnContext()
    {
        var factory = CreateFactory();

        var context = await factory.MapToAgentContextAsync(SimpleSkill(), new SkillAgentOptions());

        context.TraceScope.Should().NotBeNull();
        context.TraceScope!.ExecutionRunId.Should().NotBe(Guid.Empty);
        context.TraceScope.OptimizationRunId.Should().BeNull();
        context.TraceScope.CandidateId.Should().BeNull();
    }

    [Fact]
    public async Task CreateContext_WithTraceStoreAndNoScope_CallsStartRunAsync()
    {
        var mockWriter = Mock.Of<ITraceWriter>();
        var traceStore = new Mock<IExecutionTraceStore>();
        traceStore
            .Setup(s => s.StartRunAsync(
                It.IsAny<TraceScope>(),
                It.IsAny<RunMetadata>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockWriter);

        var factory = CreateFactory(traceStore: traceStore.Object);

        await factory.MapToAgentContextAsync(SimpleSkill(), new SkillAgentOptions());

        traceStore.Verify(s => s.StartRunAsync(
            It.Is<TraceScope>(ts => ts.OptimizationRunId == null && ts.CandidateId == null),
            It.IsAny<RunMetadata>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreateContext_WithExplicitTraceScope_UsesProvidedScope()
    {
        var mockWriter = Mock.Of<ITraceWriter>();
        var traceStore = new Mock<IExecutionTraceStore>();
        traceStore
            .Setup(s => s.StartRunAsync(
                It.IsAny<TraceScope>(),
                It.IsAny<RunMetadata>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockWriter);

        var factory = CreateFactory(traceStore: traceStore.Object);
        var expectedScope = new TraceScope
        {
            ExecutionRunId = Guid.NewGuid(),
            OptimizationRunId = Guid.NewGuid(),
            CandidateId = Guid.NewGuid()
        };
        var options = new SkillAgentOptions { TraceScope = expectedScope };

        await factory.MapToAgentContextAsync(SimpleSkill(), options);

        traceStore.Verify(s => s.StartRunAsync(
            It.Is<TraceScope>(ts =>
                ts.ExecutionRunId == expectedScope.ExecutionRunId &&
                ts.CandidateId == expectedScope.CandidateId),
            It.IsAny<RunMetadata>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // --- Temperature ---

    [Fact]
    public async Task MapToAgentContext_TemperatureFromOptions_SetOnContext()
    {
        var factory = CreateFactory();
        var options = new SkillAgentOptions { Temperature = 0.7f };

        var context = await factory.MapToAgentContextAsync(SimpleSkill(), options);

        context.Temperature.Should().Be(0.7f);
    }

    // --- AgentId ---

    [Fact]
    public async Task MapToAgentContext_AgentIdFromOptions_OverridesSkill()
    {
        var factory = CreateFactory();
        var skill = SimpleSkill();
        skill.AgentId = "skill-agent-id";
        var options = new SkillAgentOptions { AgentId = "options-agent-id" };

        var context = await factory.MapToAgentContextAsync(skill, options);

        context.AgentId.Should().Be("options-agent-id");
    }

    [Fact]
    public async Task MapToAgentContext_AgentIdFromSkill_UsedWhenOptionsNull()
    {
        var factory = CreateFactory();
        var skill = SimpleSkill();
        skill.AgentId = "skill-agent-id";

        var context = await factory.MapToAgentContextAsync(skill, new SkillAgentOptions());

        context.AgentId.Should().Be("skill-agent-id");
    }

    // --- SkillContentProvider ---

    [Fact]
    public async Task MapToAgentContext_WithSkillContentProvider_AddsToAdditionalProperties()
    {
        var provider = Mock.Of<ISkillContentProvider>();
        var factory = CreateFactory(skillContentProvider: provider);

        var context = await factory.MapToAgentContextAsync(SimpleSkill(), new SkillAgentOptions());

        context.AdditionalProperties.Should().ContainKey(ISkillContentProvider.AdditionalPropertiesKey);
    }

    // --- Description ---

    [Fact]
    public async Task MapToAgentContext_SetsDescriptionFromSkill()
    {
        var factory = CreateFactory();
        var skill = SimpleSkill();
        skill.Description = "A research agent.";

        var context = await factory.MapToAgentContextAsync(skill, new SkillAgentOptions());

        context.Description.Should().Be("A research agent.");
    }
}
