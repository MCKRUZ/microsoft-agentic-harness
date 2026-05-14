using Application.Core.CQRS.Learnings;
using Domain.AI.Learnings;
using FluentValidation.TestHelper;
using Xunit;

namespace Application.Core.Tests.CQRS.Learnings;

public sealed class LearningsCommandValidationTests
{
    private readonly RememberCommandValidator _rememberValidator = new();
    private readonly RecallQueryValidator _recallValidator = new();
    private readonly ForgetCommandValidator _forgetValidator = new();
    private readonly ImproveLearningCommandValidator _improveValidator = new();

    // == RememberCommand ==

    [Fact]
    public void Validate_RememberCommand_ValidInput_NoErrors()
    {
        var command = new RememberCommand
        {
            Content = "Always validate inputs at system boundaries",
            Category = LearningCategory.DomainKnowledge,
            Scope = new LearningScope { AgentId = "agent-1" },
            Source = new LearningSource
            {
                SourceType = LearningSourceType.HumanCorrection,
                SourceId = "session-123",
                SourceDescription = "User corrected validation approach"
            },
            Provenance = new LearningProvenance
            {
                OriginPipeline = "drift-detection",
                OriginTask = "task-456",
                OriginTimestamp = DateTimeOffset.UtcNow,
                Confidence = 0.9
            }
        };

        var result = _rememberValidator.TestValidate(command);
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_RememberCommand_EmptyContent_HasError()
    {
        var command = new RememberCommand
        {
            Content = "",
            Category = LearningCategory.DomainKnowledge,
            Scope = new LearningScope { AgentId = "agent-1" },
            Source = new LearningSource
            {
                SourceType = LearningSourceType.HumanCorrection,
                SourceId = "session-123",
                SourceDescription = "User corrected validation approach"
            },
            Provenance = new LearningProvenance
            {
                OriginPipeline = "drift-detection",
                OriginTask = "task-456",
                OriginTimestamp = DateTimeOffset.UtcNow,
                Confidence = 0.9
            }
        };

        var result = _rememberValidator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.Content);
    }

    [Fact]
    public void Validate_RememberCommand_NullContent_HasError()
    {
        var command = new RememberCommand
        {
            Content = null!,
            Category = LearningCategory.DomainKnowledge,
            Scope = new LearningScope { AgentId = "agent-1" },
            Source = new LearningSource
            {
                SourceType = LearningSourceType.HumanCorrection,
                SourceId = "session-123",
                SourceDescription = "User corrected validation approach"
            },
            Provenance = new LearningProvenance
            {
                OriginPipeline = "drift-detection",
                OriginTask = "task-456",
                OriginTimestamp = DateTimeOffset.UtcNow,
                Confidence = 0.9
            }
        };

        var result = _rememberValidator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.Content);
    }

    [Fact]
    public void Validate_RememberCommand_ScopeHasNoIdentifierAndNotGlobal_HasError()
    {
        var command = new RememberCommand
        {
            Content = "Some learning",
            Category = LearningCategory.DomainKnowledge,
            Scope = new LearningScope(),
            Source = new LearningSource
            {
                SourceType = LearningSourceType.HumanCorrection,
                SourceId = "session-123",
                SourceDescription = "User corrected validation approach"
            },
            Provenance = new LearningProvenance
            {
                OriginPipeline = "drift-detection",
                OriginTask = "task-456",
                OriginTimestamp = DateTimeOffset.UtcNow,
                Confidence = 0.9
            }
        };

        var result = _rememberValidator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.Scope);
    }

    [Fact]
    public void Validate_RememberCommand_ScopeIsGlobal_NoError()
    {
        var command = new RememberCommand
        {
            Content = "Global learning",
            Category = LearningCategory.DomainKnowledge,
            Scope = new LearningScope { IsGlobal = true },
            Source = new LearningSource
            {
                SourceType = LearningSourceType.HumanCorrection,
                SourceId = "session-123",
                SourceDescription = "User corrected validation approach"
            },
            Provenance = new LearningProvenance
            {
                OriginPipeline = "drift-detection",
                OriginTask = "task-456",
                OriginTimestamp = DateTimeOffset.UtcNow,
                Confidence = 0.9
            }
        };

        var result = _rememberValidator.TestValidate(command);
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_RememberCommand_NullSource_HasError()
    {
        var command = new RememberCommand
        {
            Content = "Some learning",
            Category = LearningCategory.DomainKnowledge,
            Scope = new LearningScope { AgentId = "agent-1" },
            Source = null!,
            Provenance = new LearningProvenance
            {
                OriginPipeline = "drift-detection",
                OriginTask = "task-456",
                OriginTimestamp = DateTimeOffset.UtcNow,
                Confidence = 0.9
            }
        };

        var result = _rememberValidator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.Source);
    }

    [Fact]
    public void Validate_RememberCommand_NullProvenance_HasError()
    {
        var command = new RememberCommand
        {
            Content = "Some learning",
            Category = LearningCategory.DomainKnowledge,
            Scope = new LearningScope { AgentId = "agent-1" },
            Source = new LearningSource
            {
                SourceType = LearningSourceType.HumanCorrection,
                SourceId = "session-123",
                SourceDescription = "User corrected validation approach"
            },
            Provenance = null!
        };

        var result = _rememberValidator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.Provenance);
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    [InlineData(2.0)]
    public void Validate_RememberCommand_ProvenanceConfidenceOutOfRange_HasError(double confidence)
    {
        var command = new RememberCommand
        {
            Content = "Some learning",
            Category = LearningCategory.DomainKnowledge,
            Scope = new LearningScope { AgentId = "agent-1" },
            Source = new LearningSource
            {
                SourceType = LearningSourceType.HumanCorrection,
                SourceId = "session-123",
                SourceDescription = "User corrected validation approach"
            },
            Provenance = new LearningProvenance
            {
                OriginPipeline = "drift-detection",
                OriginTask = "task-456",
                OriginTimestamp = DateTimeOffset.UtcNow,
                Confidence = confidence
            }
        };

        var result = _rememberValidator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.Provenance.Confidence);
    }

    // == RecallQuery ==

    [Fact]
    public void Validate_RecallQuery_ValidInput_NoErrors()
    {
        var query = new RecallQuery
        {
            Context = "How should I validate inputs?",
            Scope = new LearningScope { AgentId = "agent-1" }
        };

        var result = _recallValidator.TestValidate(query);
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_RecallQuery_EmptyContext_HasError()
    {
        var query = new RecallQuery
        {
            Context = "",
            Scope = new LearningScope { AgentId = "agent-1" }
        };

        var result = _recallValidator.TestValidate(query);
        result.ShouldHaveValidationErrorFor(x => x.Context);
    }

    [Fact]
    public void Validate_RecallQuery_ZeroMaxResults_HasError()
    {
        var query = new RecallQuery
        {
            Context = "Valid context",
            Scope = new LearningScope { AgentId = "agent-1" },
            MaxResults = 0
        };

        var result = _recallValidator.TestValidate(query);
        result.ShouldHaveValidationErrorFor(x => x.MaxResults);
    }

    [Fact]
    public void Validate_RecallQuery_EmptyScope_HasError()
    {
        var query = new RecallQuery
        {
            Context = "Valid context",
            Scope = new LearningScope()
        };

        var result = _recallValidator.TestValidate(query);
        result.ShouldHaveValidationErrorFor(x => x.Scope);
    }

    [Fact]
    public void Validate_RecallQuery_NegativeMaxResults_HasError()
    {
        var query = new RecallQuery
        {
            Context = "Valid context",
            Scope = new LearningScope { AgentId = "agent-1" },
            MaxResults = -5
        };

        var result = _recallValidator.TestValidate(query);
        result.ShouldHaveValidationErrorFor(x => x.MaxResults);
    }

    // == ForgetCommand ==

    [Fact]
    public void Validate_ForgetCommand_ValidInput_NoErrors()
    {
        var command = new ForgetCommand
        {
            LearningId = Guid.NewGuid(),
            Reason = "Outdated information"
        };

        var result = _forgetValidator.TestValidate(command);
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_ForgetCommand_EmptyGuid_HasError()
    {
        var command = new ForgetCommand
        {
            LearningId = Guid.Empty,
            Reason = "Outdated information"
        };

        var result = _forgetValidator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.LearningId);
    }

    [Fact]
    public void Validate_ForgetCommand_EmptyReason_HasError()
    {
        var command = new ForgetCommand
        {
            LearningId = Guid.NewGuid(),
            Reason = ""
        };

        var result = _forgetValidator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.Reason);
    }

    // == ImproveLearningCommand ==

    [Fact]
    public void Validate_ImproveLearningCommand_ValidInput_NoErrors()
    {
        var command = new ImproveLearningCommand
        {
            LearningId = Guid.NewGuid(),
            FeedbackScore = 4.0
        };

        var result = _improveValidator.TestValidate(command);
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_ImproveLearningCommand_FeedbackScoreBelow1_HasError()
    {
        var command = new ImproveLearningCommand
        {
            LearningId = Guid.NewGuid(),
            FeedbackScore = 0.5
        };

        var result = _improveValidator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.FeedbackScore);
    }

    [Fact]
    public void Validate_ImproveLearningCommand_FeedbackScoreAbove5_HasError()
    {
        var command = new ImproveLearningCommand
        {
            LearningId = Guid.NewGuid(),
            FeedbackScore = 5.5
        };

        var result = _improveValidator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.FeedbackScore);
    }

    [Fact]
    public void Validate_ImproveLearningCommand_EmptyGuid_HasError()
    {
        var command = new ImproveLearningCommand
        {
            LearningId = Guid.Empty,
            FeedbackScore = 3.0
        };

        var result = _improveValidator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.LearningId);
    }
}
