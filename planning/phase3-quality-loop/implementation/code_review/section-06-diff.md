diff --git a/src/Content/Application/Application.AI.Common/Interfaces/Learnings/ILearningDecayService.cs b/src/Content/Application/Application.AI.Common/Interfaces/Learnings/ILearningDecayService.cs
new file mode 100644
index 0000000..4e37224
--- /dev/null
+++ b/src/Content/Application/Application.AI.Common/Interfaces/Learnings/ILearningDecayService.cs
@@ -0,0 +1,16 @@
+using Domain.AI.Learnings;
+using Domain.Common;
+
+namespace Application.AI.Common.Interfaces.Learnings;
+
+/// <summary>
+/// Calculates temporal freshness and prunes expired learnings based on <see cref="DecayClass"/> rules.
+/// </summary>
+public interface ILearningDecayService
+{
+    /// <summary>Calculates the freshness score (0.0-1.0) for a learning based on its decay class and age.</summary>
+    Task<double> CalculateFreshnessAsync(LearningEntry learning, CancellationToken ct);
+
+    /// <summary>Soft-deletes learnings whose freshness has dropped below the configured threshold. Returns the count pruned.</summary>
+    Task<Result<int>> PruneExpiredAsync(CancellationToken ct);
+}
diff --git a/src/Content/Application/Application.AI.Common/Interfaces/Learnings/ILearningNotificationChannel.cs b/src/Content/Application/Application.AI.Common/Interfaces/Learnings/ILearningNotificationChannel.cs
new file mode 100644
index 0000000..7333262
--- /dev/null
+++ b/src/Content/Application/Application.AI.Common/Interfaces/Learnings/ILearningNotificationChannel.cs
@@ -0,0 +1,16 @@
+using Domain.AI.Learnings;
+
+namespace Application.AI.Common.Interfaces.Learnings;
+
+/// <summary>
+/// Notification channel for learning lifecycle events.
+/// Implementations include AG-UI SSE, logging, and audit sinks.
+/// </summary>
+public interface ILearningNotificationChannel
+{
+    /// <summary>Notifies that a new learning has been captured.</summary>
+    Task NotifyLearningCapturedAsync(LearningEntry learning, CancellationToken ct);
+
+    /// <summary>Notifies that a learning was applied during agent execution.</summary>
+    Task NotifyLearningAppliedAsync(LearningEntry learning, string agentId, CancellationToken ct);
+}
diff --git a/src/Content/Application/Application.AI.Common/Interfaces/Learnings/ILearningsStore.cs b/src/Content/Application/Application.AI.Common/Interfaces/Learnings/ILearningsStore.cs
new file mode 100644
index 0000000..281fcce
--- /dev/null
+++ b/src/Content/Application/Application.AI.Common/Interfaces/Learnings/ILearningsStore.cs
@@ -0,0 +1,26 @@
+using Domain.AI.Learnings;
+using Domain.Common;
+
+namespace Application.AI.Common.Interfaces.Learnings;
+
+/// <summary>
+/// Persistence contract for learning entries.
+/// Keyed DI: <c>"graph"</c> (default), <c>"in_memory"</c> (testing).
+/// </summary>
+public interface ILearningsStore
+{
+    /// <summary>Persists a new learning entry.</summary>
+    Task<Result> SaveAsync(LearningEntry learning, CancellationToken ct);
+
+    /// <summary>Retrieves a learning by ID. Returns null value when not found.</summary>
+    Task<Result<LearningEntry?>> GetAsync(Guid learningId, CancellationToken ct);
+
+    /// <summary>Searches learnings matching the specified criteria with scope-aware filtering.</summary>
+    Task<Result<IReadOnlyList<LearningEntry>>> SearchAsync(LearningSearchCriteria criteria, CancellationToken ct);
+
+    /// <summary>Updates an existing learning entry (feedback weight, access time, etc.).</summary>
+    Task<Result> UpdateAsync(LearningEntry learning, CancellationToken ct);
+
+    /// <summary>Marks a learning as soft-deleted with a reason. Excluded from future searches.</summary>
+    Task<Result> SoftDeleteAsync(Guid learningId, string reason, CancellationToken ct);
+}
diff --git a/src/Content/Application/Application.AI.Common/Interfaces/Learnings/LearningSearchCriteria.cs b/src/Content/Application/Application.AI.Common/Interfaces/Learnings/LearningSearchCriteria.cs
new file mode 100644
index 0000000..45ef342
--- /dev/null
+++ b/src/Content/Application/Application.AI.Common/Interfaces/Learnings/LearningSearchCriteria.cs
@@ -0,0 +1,25 @@
+using Domain.AI.Learnings;
+
+namespace Application.AI.Common.Interfaces.Learnings;
+
+/// <summary>
+/// Filter criteria for searching learning entries. Used by <see cref="ILearningsStore"/>.
+/// All optional fields act as AND filters when provided.
+/// </summary>
+public sealed record LearningSearchCriteria
+{
+    /// <summary>Required scope for filtering. Agent-scoped queries also return team and global learnings.</summary>
+    public required LearningScope Scope { get; init; }
+
+    /// <summary>Filter by learning category. Null returns all categories.</summary>
+    public LearningCategory? Category { get; init; }
+
+    /// <summary>Minimum feedback weight threshold. Null disables weight filtering.</summary>
+    public double? MinFeedbackWeight { get; init; }
+
+    /// <summary>Only return learnings created after this date. Null disables.</summary>
+    public DateTimeOffset? CreatedAfter { get; init; }
+
+    /// <summary>Only return learnings created before this date. Null disables.</summary>
+    public DateTimeOffset? CreatedBefore { get; init; }
+}
diff --git a/src/Content/Application/Application.Core/CQRS/Learnings/ForgetCommand.cs b/src/Content/Application/Application.Core/CQRS/Learnings/ForgetCommand.cs
new file mode 100644
index 0000000..a2160a0
--- /dev/null
+++ b/src/Content/Application/Application.Core/CQRS/Learnings/ForgetCommand.cs
@@ -0,0 +1,17 @@
+using Domain.Common;
+using MediatR;
+
+namespace Application.Core.CQRS.Learnings;
+
+/// <summary>
+/// Soft-deletes a learning entry. The learning remains in the graph for audit
+/// but is excluded from future search results.
+/// </summary>
+public sealed record ForgetCommand : IRequest<Result>
+{
+    /// <summary>ID of the learning to soft-delete.</summary>
+    public required Guid LearningId { get; init; }
+
+    /// <summary>Reason for deletion (audit trail).</summary>
+    public required string Reason { get; init; }
+}
diff --git a/src/Content/Application/Application.Core/CQRS/Learnings/ForgetCommandValidator.cs b/src/Content/Application/Application.Core/CQRS/Learnings/ForgetCommandValidator.cs
new file mode 100644
index 0000000..e8c36e2
--- /dev/null
+++ b/src/Content/Application/Application.Core/CQRS/Learnings/ForgetCommandValidator.cs
@@ -0,0 +1,18 @@
+using FluentValidation;
+
+namespace Application.Core.CQRS.Learnings;
+
+/// <summary>
+/// Validates <see cref="ForgetCommand"/>: LearningId not empty, Reason not empty.
+/// </summary>
+public sealed class ForgetCommandValidator : AbstractValidator<ForgetCommand>
+{
+    public ForgetCommandValidator()
+    {
+        RuleFor(x => x.LearningId)
+            .NotEqual(Guid.Empty).WithMessage("LearningId must not be empty.");
+
+        RuleFor(x => x.Reason)
+            .NotEmpty().WithMessage("Reason must not be empty.");
+    }
+}
diff --git a/src/Content/Application/Application.Core/CQRS/Learnings/ImproveLearningCommand.cs b/src/Content/Application/Application.Core/CQRS/Learnings/ImproveLearningCommand.cs
new file mode 100644
index 0000000..7c97a3e
--- /dev/null
+++ b/src/Content/Application/Application.Core/CQRS/Learnings/ImproveLearningCommand.cs
@@ -0,0 +1,20 @@
+using Domain.AI.Learnings;
+using Domain.Common;
+using MediatR;
+
+namespace Application.Core.CQRS.Learnings;
+
+/// <summary>
+/// Updates a learning's feedback weight via EMA and optionally reinforces its content.
+/// </summary>
+public sealed record ImproveLearningCommand : IRequest<Result<LearningEntry>>
+{
+    /// <summary>ID of the learning to improve.</summary>
+    public required Guid LearningId { get; init; }
+
+    /// <summary>Feedback score (1.0-5.0). Incorporated into the learning's EMA weight.</summary>
+    public required double FeedbackScore { get; init; }
+
+    /// <summary>Optional updated content to reinforce the learning.</summary>
+    public string? ReinforcementContent { get; init; }
+}
diff --git a/src/Content/Application/Application.Core/CQRS/Learnings/ImproveLearningCommandValidator.cs b/src/Content/Application/Application.Core/CQRS/Learnings/ImproveLearningCommandValidator.cs
new file mode 100644
index 0000000..677d2c2
--- /dev/null
+++ b/src/Content/Application/Application.Core/CQRS/Learnings/ImproveLearningCommandValidator.cs
@@ -0,0 +1,18 @@
+using FluentValidation;
+
+namespace Application.Core.CQRS.Learnings;
+
+/// <summary>
+/// Validates <see cref="ImproveLearningCommand"/>: LearningId not empty, FeedbackScore in [1.0, 5.0].
+/// </summary>
+public sealed class ImproveLearningCommandValidator : AbstractValidator<ImproveLearningCommand>
+{
+    public ImproveLearningCommandValidator()
+    {
+        RuleFor(x => x.LearningId)
+            .NotEqual(Guid.Empty).WithMessage("LearningId must not be empty.");
+
+        RuleFor(x => x.FeedbackScore)
+            .InclusiveBetween(1.0, 5.0).WithMessage("FeedbackScore must be between 1.0 and 5.0.");
+    }
+}
diff --git a/src/Content/Application/Application.Core/CQRS/Learnings/RecallQuery.cs b/src/Content/Application/Application.Core/CQRS/Learnings/RecallQuery.cs
new file mode 100644
index 0000000..ac40189
--- /dev/null
+++ b/src/Content/Application/Application.Core/CQRS/Learnings/RecallQuery.cs
@@ -0,0 +1,23 @@
+using Domain.AI.Learnings;
+using Domain.Common;
+using MediatR;
+
+namespace Application.Core.CQRS.Learnings;
+
+/// <summary>
+/// Retrieves learnings relevant to the given context, ranked by relevance and feedback weight.
+/// </summary>
+public sealed record RecallQuery : IRequest<Result<IReadOnlyList<WeightedLearning>>>
+{
+    /// <summary>Natural language context to match against stored learnings.</summary>
+    public required string Context { get; init; }
+
+    /// <summary>Scope for filtering (includes hierarchical scope resolution).</summary>
+    public required LearningScope Scope { get; init; }
+
+    /// <summary>Maximum number of results to return. Default 10.</summary>
+    public int MaxResults { get; init; } = 10;
+
+    /// <summary>Minimum relevance score threshold (0.0-1.0). Default 0.0 (no filter).</summary>
+    public double MinRelevance { get; init; } = 0.0;
+}
diff --git a/src/Content/Application/Application.Core/CQRS/Learnings/RecallQueryValidator.cs b/src/Content/Application/Application.Core/CQRS/Learnings/RecallQueryValidator.cs
new file mode 100644
index 0000000..391700a
--- /dev/null
+++ b/src/Content/Application/Application.Core/CQRS/Learnings/RecallQueryValidator.cs
@@ -0,0 +1,21 @@
+using FluentValidation;
+
+namespace Application.Core.CQRS.Learnings;
+
+/// <summary>
+/// Validates <see cref="RecallQuery"/>: context not empty, MaxResults positive, MinRelevance in [0,1].
+/// </summary>
+public sealed class RecallQueryValidator : AbstractValidator<RecallQuery>
+{
+    public RecallQueryValidator()
+    {
+        RuleFor(x => x.Context)
+            .NotEmpty().WithMessage("Context must not be empty.");
+
+        RuleFor(x => x.MaxResults)
+            .GreaterThan(0).WithMessage("MaxResults must be greater than 0.");
+
+        RuleFor(x => x.MinRelevance)
+            .InclusiveBetween(0.0, 1.0).WithMessage("MinRelevance must be between 0.0 and 1.0.");
+    }
+}
diff --git a/src/Content/Application/Application.Core/CQRS/Learnings/RecordLearningAccessCommand.cs b/src/Content/Application/Application.Core/CQRS/Learnings/RecordLearningAccessCommand.cs
new file mode 100644
index 0000000..68ccbfa
--- /dev/null
+++ b/src/Content/Application/Application.Core/CQRS/Learnings/RecordLearningAccessCommand.cs
@@ -0,0 +1,17 @@
+using Domain.Common;
+using MediatR;
+
+namespace Application.Core.CQRS.Learnings;
+
+/// <summary>
+/// Fire-and-forget command dispatched by RecallQueryHandler for CQRS-clean access tracking.
+/// Updates <c>LastAccessedAt</c> on retrieved learning entries.
+/// </summary>
+public sealed record RecordLearningAccessCommand : IRequest<Result>
+{
+    /// <summary>IDs of the learnings that were accessed during recall.</summary>
+    public required IReadOnlyList<Guid> LearningIds { get; init; }
+
+    /// <summary>When the access occurred.</summary>
+    public required DateTimeOffset AccessedAt { get; init; }
+}
diff --git a/src/Content/Application/Application.Core/CQRS/Learnings/RememberCommand.cs b/src/Content/Application/Application.Core/CQRS/Learnings/RememberCommand.cs
new file mode 100644
index 0000000..304c219
--- /dev/null
+++ b/src/Content/Application/Application.Core/CQRS/Learnings/RememberCommand.cs
@@ -0,0 +1,29 @@
+using Domain.AI.Learnings;
+using Domain.Common;
+using MediatR;
+
+namespace Application.Core.CQRS.Learnings;
+
+/// <summary>
+/// Captures a new learning from corrections, drift events, escalation resolutions, or manual entries.
+/// </summary>
+public sealed record RememberCommand : IRequest<Result<LearningEntry>>
+{
+    /// <summary>The knowledge content to persist.</summary>
+    public required string Content { get; init; }
+
+    /// <summary>What kind of knowledge this learning represents.</summary>
+    public required LearningCategory Category { get; init; }
+
+    /// <summary>Visibility scope (agent, team, or global).</summary>
+    public required LearningScope Scope { get; init; }
+
+    /// <summary>What produced this learning.</summary>
+    public required LearningSource Source { get; init; }
+
+    /// <summary>Pipeline provenance metadata.</summary>
+    public required LearningProvenance Provenance { get; init; }
+
+    /// <summary>Override the default decay class. Null uses the category default.</summary>
+    public DecayClass? DecayClass { get; init; }
+}
diff --git a/src/Content/Application/Application.Core/CQRS/Learnings/RememberCommandValidator.cs b/src/Content/Application/Application.Core/CQRS/Learnings/RememberCommandValidator.cs
new file mode 100644
index 0000000..35f86da
--- /dev/null
+++ b/src/Content/Application/Application.Core/CQRS/Learnings/RememberCommandValidator.cs
@@ -0,0 +1,19 @@
+using FluentValidation;
+
+namespace Application.Core.CQRS.Learnings;
+
+/// <summary>
+/// Validates <see cref="RememberCommand"/>: content not empty, scope must have at least one identifier or be global.
+/// </summary>
+public sealed class RememberCommandValidator : AbstractValidator<RememberCommand>
+{
+    public RememberCommandValidator()
+    {
+        RuleFor(x => x.Content)
+            .NotEmpty().WithMessage("Content must not be empty.");
+
+        RuleFor(x => x.Scope)
+            .Must(s => s.IsGlobal || !string.IsNullOrEmpty(s.AgentId) || !string.IsNullOrEmpty(s.TeamId))
+            .WithMessage("Scope must have AgentId, TeamId, or IsGlobal set.");
+    }
+}
diff --git a/src/Content/Tests/Application.Core.Tests/CQRS/Learnings/LearningSearchCriteriaTests.cs b/src/Content/Tests/Application.Core.Tests/CQRS/Learnings/LearningSearchCriteriaTests.cs
new file mode 100644
index 0000000..e67f362
--- /dev/null
+++ b/src/Content/Tests/Application.Core.Tests/CQRS/Learnings/LearningSearchCriteriaTests.cs
@@ -0,0 +1,41 @@
+using Application.AI.Common.Interfaces.Learnings;
+using Domain.AI.Learnings;
+using FluentAssertions;
+using Xunit;
+
+namespace Application.Core.Tests.CQRS.Learnings;
+
+public sealed class LearningSearchCriteriaTests
+{
+    [Fact]
+    public void LearningSearchCriteria_DefaultConstruction_HasNullFilters()
+    {
+        var criteria = new LearningSearchCriteria
+        {
+            Scope = new LearningScope { AgentId = "agent-1" }
+        };
+
+        criteria.Category.Should().BeNull();
+        criteria.MinFeedbackWeight.Should().BeNull();
+        criteria.CreatedAfter.Should().BeNull();
+        criteria.CreatedBefore.Should().BeNull();
+    }
+
+    [Fact]
+    public void LearningSearchCriteria_ScopeHierarchyPrecedence_AgentFirst()
+    {
+        var criteria = new LearningSearchCriteria
+        {
+            Scope = new LearningScope
+            {
+                AgentId = "agent-1",
+                TeamId = "team-alpha",
+                IsGlobal = true
+            }
+        };
+
+        criteria.Scope.AgentId.Should().Be("agent-1");
+        criteria.Scope.TeamId.Should().Be("team-alpha");
+        criteria.Scope.IsGlobal.Should().BeTrue();
+    }
+}
diff --git a/src/Content/Tests/Application.Core.Tests/CQRS/Learnings/LearningsCommandValidationTests.cs b/src/Content/Tests/Application.Core.Tests/CQRS/Learnings/LearningsCommandValidationTests.cs
new file mode 100644
index 0000000..d0f8fe9
--- /dev/null
+++ b/src/Content/Tests/Application.Core.Tests/CQRS/Learnings/LearningsCommandValidationTests.cs
@@ -0,0 +1,302 @@
+using Application.Core.CQRS.Learnings;
+using Domain.AI.Learnings;
+using FluentValidation.TestHelper;
+using Xunit;
+
+namespace Application.Core.Tests.CQRS.Learnings;
+
+public sealed class LearningsCommandValidationTests
+{
+    private readonly RememberCommandValidator _rememberValidator = new();
+    private readonly RecallQueryValidator _recallValidator = new();
+    private readonly ForgetCommandValidator _forgetValidator = new();
+    private readonly ImproveLearningCommandValidator _improveValidator = new();
+
+    // == RememberCommand ==
+
+    [Fact]
+    public void Validate_RememberCommand_ValidInput_NoErrors()
+    {
+        var command = new RememberCommand
+        {
+            Content = "Always validate inputs at system boundaries",
+            Category = LearningCategory.DomainKnowledge,
+            Scope = new LearningScope { AgentId = "agent-1" },
+            Source = new LearningSource
+            {
+                SourceType = LearningSourceType.HumanCorrection,
+                SourceId = "session-123",
+                SourceDescription = "User corrected validation approach"
+            },
+            Provenance = new LearningProvenance
+            {
+                OriginPipeline = "drift-detection",
+                OriginTask = "task-456",
+                OriginTimestamp = DateTimeOffset.UtcNow,
+                Confidence = 0.9
+            }
+        };
+
+        var result = _rememberValidator.TestValidate(command);
+        result.ShouldNotHaveAnyValidationErrors();
+    }
+
+    [Fact]
+    public void Validate_RememberCommand_EmptyContent_HasError()
+    {
+        var command = new RememberCommand
+        {
+            Content = "",
+            Category = LearningCategory.DomainKnowledge,
+            Scope = new LearningScope { AgentId = "agent-1" },
+            Source = new LearningSource
+            {
+                SourceType = LearningSourceType.HumanCorrection,
+                SourceId = "session-123",
+                SourceDescription = "User corrected validation approach"
+            },
+            Provenance = new LearningProvenance
+            {
+                OriginPipeline = "drift-detection",
+                OriginTask = "task-456",
+                OriginTimestamp = DateTimeOffset.UtcNow,
+                Confidence = 0.9
+            }
+        };
+
+        var result = _rememberValidator.TestValidate(command);
+        result.ShouldHaveValidationErrorFor(x => x.Content);
+    }
+
+    [Fact]
+    public void Validate_RememberCommand_NullContent_HasError()
+    {
+        var command = new RememberCommand
+        {
+            Content = null!,
+            Category = LearningCategory.DomainKnowledge,
+            Scope = new LearningScope { AgentId = "agent-1" },
+            Source = new LearningSource
+            {
+                SourceType = LearningSourceType.HumanCorrection,
+                SourceId = "session-123",
+                SourceDescription = "User corrected validation approach"
+            },
+            Provenance = new LearningProvenance
+            {
+                OriginPipeline = "drift-detection",
+                OriginTask = "task-456",
+                OriginTimestamp = DateTimeOffset.UtcNow,
+                Confidence = 0.9
+            }
+        };
+
+        var result = _rememberValidator.TestValidate(command);
+        result.ShouldHaveValidationErrorFor(x => x.Content);
+    }
+
+    [Fact]
+    public void Validate_RememberCommand_ScopeHasNoIdentifierAndNotGlobal_HasError()
+    {
+        var command = new RememberCommand
+        {
+            Content = "Some learning",
+            Category = LearningCategory.DomainKnowledge,
+            Scope = new LearningScope(),
+            Source = new LearningSource
+            {
+                SourceType = LearningSourceType.HumanCorrection,
+                SourceId = "session-123",
+                SourceDescription = "User corrected validation approach"
+            },
+            Provenance = new LearningProvenance
+            {
+                OriginPipeline = "drift-detection",
+                OriginTask = "task-456",
+                OriginTimestamp = DateTimeOffset.UtcNow,
+                Confidence = 0.9
+            }
+        };
+
+        var result = _rememberValidator.TestValidate(command);
+        result.ShouldHaveValidationErrorFor(x => x.Scope);
+    }
+
+    [Fact]
+    public void Validate_RememberCommand_ScopeIsGlobal_NoError()
+    {
+        var command = new RememberCommand
+        {
+            Content = "Global learning",
+            Category = LearningCategory.DomainKnowledge,
+            Scope = new LearningScope { IsGlobal = true },
+            Source = new LearningSource
+            {
+                SourceType = LearningSourceType.HumanCorrection,
+                SourceId = "session-123",
+                SourceDescription = "User corrected validation approach"
+            },
+            Provenance = new LearningProvenance
+            {
+                OriginPipeline = "drift-detection",
+                OriginTask = "task-456",
+                OriginTimestamp = DateTimeOffset.UtcNow,
+                Confidence = 0.9
+            }
+        };
+
+        var result = _rememberValidator.TestValidate(command);
+        result.ShouldNotHaveAnyValidationErrors();
+    }
+
+    // == RecallQuery ==
+
+    [Fact]
+    public void Validate_RecallQuery_ValidInput_NoErrors()
+    {
+        var query = new RecallQuery
+        {
+            Context = "How should I validate inputs?",
+            Scope = new LearningScope { AgentId = "agent-1" }
+        };
+
+        var result = _recallValidator.TestValidate(query);
+        result.ShouldNotHaveAnyValidationErrors();
+    }
+
+    [Fact]
+    public void Validate_RecallQuery_EmptyContext_HasError()
+    {
+        var query = new RecallQuery
+        {
+            Context = "",
+            Scope = new LearningScope { AgentId = "agent-1" }
+        };
+
+        var result = _recallValidator.TestValidate(query);
+        result.ShouldHaveValidationErrorFor(x => x.Context);
+    }
+
+    [Fact]
+    public void Validate_RecallQuery_ZeroMaxResults_HasError()
+    {
+        var query = new RecallQuery
+        {
+            Context = "Valid context",
+            Scope = new LearningScope { AgentId = "agent-1" },
+            MaxResults = 0
+        };
+
+        var result = _recallValidator.TestValidate(query);
+        result.ShouldHaveValidationErrorFor(x => x.MaxResults);
+    }
+
+    [Fact]
+    public void Validate_RecallQuery_NegativeMaxResults_HasError()
+    {
+        var query = new RecallQuery
+        {
+            Context = "Valid context",
+            Scope = new LearningScope { AgentId = "agent-1" },
+            MaxResults = -5
+        };
+
+        var result = _recallValidator.TestValidate(query);
+        result.ShouldHaveValidationErrorFor(x => x.MaxResults);
+    }
+
+    // == ForgetCommand ==
+
+    [Fact]
+    public void Validate_ForgetCommand_ValidInput_NoErrors()
+    {
+        var command = new ForgetCommand
+        {
+            LearningId = Guid.NewGuid(),
+            Reason = "Outdated information"
+        };
+
+        var result = _forgetValidator.TestValidate(command);
+        result.ShouldNotHaveAnyValidationErrors();
+    }
+
+    [Fact]
+    public void Validate_ForgetCommand_EmptyGuid_HasError()
+    {
+        var command = new ForgetCommand
+        {
+            LearningId = Guid.Empty,
+            Reason = "Outdated information"
+        };
+
+        var result = _forgetValidator.TestValidate(command);
+        result.ShouldHaveValidationErrorFor(x => x.LearningId);
+    }
+
+    [Fact]
+    public void Validate_ForgetCommand_EmptyReason_HasError()
+    {
+        var command = new ForgetCommand
+        {
+            LearningId = Guid.NewGuid(),
+            Reason = ""
+        };
+
+        var result = _forgetValidator.TestValidate(command);
+        result.ShouldHaveValidationErrorFor(x => x.Reason);
+    }
+
+    // == ImproveLearningCommand ==
+
+    [Fact]
+    public void Validate_ImproveLearningCommand_ValidInput_NoErrors()
+    {
+        var command = new ImproveLearningCommand
+        {
+            LearningId = Guid.NewGuid(),
+            FeedbackScore = 4.0
+        };
+
+        var result = _improveValidator.TestValidate(command);
+        result.ShouldNotHaveAnyValidationErrors();
+    }
+
+    [Fact]
+    public void Validate_ImproveLearningCommand_FeedbackScoreBelow1_HasError()
+    {
+        var command = new ImproveLearningCommand
+        {
+            LearningId = Guid.NewGuid(),
+            FeedbackScore = 0.5
+        };
+
+        var result = _improveValidator.TestValidate(command);
+        result.ShouldHaveValidationErrorFor(x => x.FeedbackScore);
+    }
+
+    [Fact]
+    public void Validate_ImproveLearningCommand_FeedbackScoreAbove5_HasError()
+    {
+        var command = new ImproveLearningCommand
+        {
+            LearningId = Guid.NewGuid(),
+            FeedbackScore = 5.5
+        };
+
+        var result = _improveValidator.TestValidate(command);
+        result.ShouldHaveValidationErrorFor(x => x.FeedbackScore);
+    }
+
+    [Fact]
+    public void Validate_ImproveLearningCommand_EmptyGuid_HasError()
+    {
+        var command = new ImproveLearningCommand
+        {
+            LearningId = Guid.Empty,
+            FeedbackScore = 3.0
+        };
+
+        var result = _improveValidator.TestValidate(command);
+        result.ShouldHaveValidationErrorFor(x => x.LearningId);
+    }
+}
