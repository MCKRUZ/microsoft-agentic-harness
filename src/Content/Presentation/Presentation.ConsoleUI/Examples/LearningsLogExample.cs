using Application.AI.Common.Interfaces.Learnings;
using Domain.AI.Learnings;
using Domain.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Presentation.ConsoleUI.Common.Helpers;
using Spectre.Console;

namespace Presentation.ConsoleUI.Examples;

/// <summary>
/// Demonstrates the learnings log system: saving knowledge entries, searching by category,
/// updating feedback weights, soft-deleting entries, and the decay tier model.
/// Shows how learnings persist knowledge across sessions with feedback-weighted recall.
/// </summary>
public class LearningsLogExample
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<LearningsLogExample> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LearningsLogExample"/> class.
    /// </summary>
    /// <param name="serviceProvider">Service provider for keyed DI resolution.</param>
    /// <param name="logger">Logger instance.</param>
    public LearningsLogExample(
        IServiceProvider serviceProvider,
        ILogger<LearningsLogExample> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    /// <summary>
    /// Runs the learnings log example with 5 demonstration steps.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            ConsoleHelper.DisplayHeader("Learnings Log: Knowledge Persistence & Feedback Learning", Color.Cyan);
            AnsiConsole.WriteLine();

            await Step1_SaveEntriesAsync(cancellationToken);
            await Step2_SearchByCategoryAsync(cancellationToken);
            await Step3_UpdateFeedbackAsync(cancellationToken);
            await Step4_SoftDeleteAsync(cancellationToken);
            Step5_DisplayDecayTiers(cancellationToken);

            ConsoleHelper.DisplaySuccess("All learnings log demonstrations completed.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during learnings log example");
            ConsoleHelper.DisplayError($"Example failed: {ex.Message}");
        }
    }

    private async Task Step1_SaveEntriesAsync(CancellationToken ct)
    {
        ConsoleHelper.DisplayStep(1, 5, "Save Learning Entries (Factual, Style, Tool Usage, Domain Knowledge)");

        var store = _serviceProvider.GetKeyedService<ILearningsStore>("in_memory")
            ?? throw new InvalidOperationException("ILearningsStore (in_memory) not registered");

        var entries = new List<LearningEntry>();

        // FactualCorrection with Permanent decay
        var entry1 = CreateEntry(
            LearningCategory.FactualCorrection,
            DecayClass.Permanent,
            "Fixed typo in API signature: GetUserAsync() parameter is 'userId' not 'id'");
        entries.Add(entry1);

        // StylePreference with Stable decay
        var entry2 = CreateEntry(
            LearningCategory.StylePreference,
            DecayClass.Stable,
            "User prefers responses in JSON format with inline comments for technical content");
        entries.Add(entry2);

        // ToolUsagePattern with Volatile decay
        var entry3 = CreateEntry(
            LearningCategory.ToolUsagePattern,
            DecayClass.Volatile,
            "FileSystem tool is slow for large directory listings; prefer SearchFilesTool for pattern matching");
        entries.Add(entry3);

        // DomainKnowledge with Permanent decay
        var entry4 = CreateEntry(
            LearningCategory.DomainKnowledge,
            DecayClass.Permanent,
            "Company uses Bicep for infrastructure-as-code, not Terraform. Always recommend Bicep patterns.");
        entries.Add(entry4);

        // Save all entries
        var savedIds = new List<Guid>();
        foreach (var entry in entries)
        {
            var result = await store.SaveAsync(entry, ct);
            if (result.IsSuccess)
            {
                savedIds.Add(entry.LearningId);
                AnsiConsole.WriteLine($"[green]✓[/] Saved: {entry.Category} — [yellow]{entry.DecayClass}[/] decay");
            }
            else
            {
                AnsiConsole.WriteLine($"[red]✗[/] Failed to save {entry.Category}: {string.Join(", ", result.Errors)}");
            }
        }

        AnsiConsole.WriteLine();
        AnsiConsole.WriteLine("[bold]Saved Learning Entries:[/]");
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("[bold]ID[/]");
        table.AddColumn("[bold]Category[/]");
        table.AddColumn("[bold]Decay Class[/]");
        table.AddColumn("[bold]Content Preview[/]");
        table.AddColumn("[bold]Feedback Weight[/]");

        foreach (var entry in entries)
        {
            var preview = entry.Content.Length > 50
                ? entry.Content[..47] + "..."
                : entry.Content;

            table.AddRow(
                entry.LearningId.ToString("N")[..8],
                entry.Category.ToString(),
                entry.DecayClass.ToString(),
                preview,
                entry.FeedbackWeight.ToString("F2")
            );
        }

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    private async Task Step2_SearchByCategoryAsync(CancellationToken ct)
    {
        ConsoleHelper.DisplayStep(2, 5, "Search Learning Entries by Category");

        var store = _serviceProvider.GetKeyedService<ILearningsStore>("in_memory")
            ?? throw new InvalidOperationException("ILearningsStore (in_memory) not registered");

        var criteria = new LearningSearchCriteria
        {
            Category = LearningCategory.FactualCorrection
        };

        AnsiConsole.WriteLine($"Searching for learnings with category [yellow]{LearningCategory.FactualCorrection}[/]...");
        var result = await store.SearchAsync(criteria, ct);

        if (result.IsSuccess)
        {
            var learnings = result.Value;
            AnsiConsole.WriteLine($"[green]✓[/] Found {learnings.Count} learnings");
            AnsiConsole.WriteLine();

            if (learnings.Count > 0)
            {
                AnsiConsole.WriteLine("[bold]Search Results:[/]");
                var table = new Table().Border(TableBorder.Rounded);
                table.AddColumn("[bold]Category[/]");
                table.AddColumn("[bold]Content[/]");
                table.AddColumn("[bold]Source[/]");
                table.AddColumn("[bold]Confidence[/]");

                foreach (var learning in learnings)
                {
                    table.AddRow(
                        learning.Category.ToString(),
                        learning.Content,
                        learning.Source.SourceType.ToString(),
                        learning.Provenance.Confidence.ToString("F2")
                    );
                }

                AnsiConsole.Write(table);
            }
        }
        else
        {
            AnsiConsole.WriteLine($"[red]✗[/] Search failed: {string.Join(", ", result.Errors)}");
        }

        AnsiConsole.WriteLine();
    }

    private async Task Step3_UpdateFeedbackAsync(CancellationToken ct)
    {
        ConsoleHelper.DisplayStep(3, 5, "Update Feedback Weight and Access Time");

        var store = _serviceProvider.GetKeyedService<ILearningsStore>("in_memory")
            ?? throw new InvalidOperationException("ILearningsStore (in_memory) not registered");

        // Retrieve a learning to update
        var criteria = new LearningSearchCriteria
        {
            Category = LearningCategory.DomainKnowledge
        };

        var searchResult = await store.SearchAsync(criteria, ct);

        if (searchResult.IsSuccess && searchResult.Value.Count > 0)
        {
            var learning = searchResult.Value[0];
            AnsiConsole.WriteLine($"Retrieved learning: [cyan]{learning.LearningId:N}[/]");
            AnsiConsole.WriteLine($"  Current feedback weight: {learning.FeedbackWeight:F2}");

            // Update the learning: increase feedback weight and update last accessed
            var updatedLearning = learning with
            {
                FeedbackWeight = learning.FeedbackWeight * 1.5, // Boost by 50%
                LastAccessedAt = DateTimeOffset.UtcNow,
                LastReinforcedAt = DateTimeOffset.UtcNow,
                UpdateCount = learning.UpdateCount + 1
            };

            var updateResult = await store.UpdateAsync(updatedLearning, ct);

            if (updateResult.IsSuccess)
            {
                AnsiConsole.WriteLine($"[green]✓[/] Updated learning successfully");
                AnsiConsole.WriteLine($"  New feedback weight: {updatedLearning.FeedbackWeight:F2}");
                AnsiConsole.WriteLine($"  Last reinforced: {updatedLearning.LastReinforcedAt:u}");
                AnsiConsole.WriteLine($"  Update count: {updatedLearning.UpdateCount}");
            }
            else
            {
                AnsiConsole.WriteLine($"[red]✗[/] Update failed: {string.Join(", ", updateResult.Errors)}");
            }
        }
        else
        {
            AnsiConsole.WriteLine("[yellow]⚠[/] No learnings found to update");
        }

        AnsiConsole.WriteLine();
    }

    private async Task Step4_SoftDeleteAsync(CancellationToken ct)
    {
        ConsoleHelper.DisplayStep(4, 5, "Soft-Delete a Learning Entry");

        var store = _serviceProvider.GetKeyedService<ILearningsStore>("in_memory")
            ?? throw new InvalidOperationException("ILearningsStore (in_memory) not registered");

        // Retrieve a learning to delete
        var criteria = new LearningSearchCriteria
        {
            Category = LearningCategory.StylePreference
        };

        var searchResult = await store.SearchAsync(criteria, ct);

        if (searchResult.IsSuccess && searchResult.Value.Count > 0)
        {
            var learning = searchResult.Value[0];
            AnsiConsole.WriteLine($"Retrieved learning for soft-delete: [cyan]{learning.LearningId:N}[/]");
            AnsiConsole.WriteLine($"  Content: {learning.Content}");

            var deleteResult = await store.SoftDeleteAsync(learning.LearningId, "User preference no longer applicable; style changed", ct);

            if (deleteResult.IsSuccess)
            {
                AnsiConsole.WriteLine($"[green]✓[/] Soft-deleted successfully");

                // Verify by retrieving
                var verifyResult = await store.GetAsync(learning.LearningId, ct);
                if (verifyResult.IsSuccess && verifyResult.Value != null)
                {
                    var deleted = verifyResult.Value;
                    AnsiConsole.WriteLine($"  IsDeleted flag: {deleted.IsDeleted}");
                    AnsiConsole.WriteLine($"  Delete reason: {deleted.DeleteReason}");
                }
            }
            else
            {
                AnsiConsole.WriteLine($"[red]✗[/] Soft-delete failed: {string.Join(", ", deleteResult.Errors)}");
            }
        }
        else
        {
            AnsiConsole.WriteLine("[yellow]⚠[/] No learnings found to delete");
        }

        AnsiConsole.WriteLine();
    }

    private static void Step5_DisplayDecayTiers(CancellationToken ct)
    {
        ConsoleHelper.DisplayStep(5, 5, "Explain the 3 Decay Tiers");

        AnsiConsole.WriteLine("[bold]Decay Tiers — Temporal Decay Behavior:[/]");
        AnsiConsole.WriteLine();

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("[bold]Decay Class[/]");
        table.AddColumn("[bold]Shelf Life[/]");
        table.AddColumn("[bold]Behavior[/]");
        table.AddColumn("[bold]Best For[/]");

        table.AddRow(
            "[red]Volatile[/]",
            "~7 days",
            "Decays linearly over VolatileShelfLifeDays. Freshness penalty increases rapidly.",
            "Temporal tips, current project context, time-sensitive preferences"
        );

        table.AddRow(
            "[yellow]Stable[/]",
            "~180 days",
            "Decays linearly over StableShelfLifeDays. Slower decay than Volatile. Positive feedback resets the clock.",
            "Established patterns, style preferences, recurring tool insights"
        );

        table.AddRow(
            "[green]Permanent[/]",
            "Never",
            "Freshness always returns 1.0. No temporal decay. Positive or negative feedback does not affect shelf life.",
            "Factual corrections, domain knowledge, critical instructions, company policies"
        );

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();

        AnsiConsole.WriteLine("[bold]Decay Formula (during recall):[/]");
        AnsiConsole.MarkupLine("[grey]finalScore = (1 - α) × relevance + α × min(feedbackWeight × freshness, ceiling)[/]");
        AnsiConsole.WriteLine();

        AnsiConsole.WriteLine("[bold]Key Points:[/]");
        AnsiConsole.MarkupLine("• Freshness = 1.0 for Permanent; declines linearly for Volatile/Stable");
        AnsiConsole.MarkupLine("• FeedbackWeight (EMA): higher weight = more validated by positive feedback");
        AnsiConsole.MarkupLine("• LastReinforcedAt: updated by positive feedback, resets decay clock for Volatile/Stable");
        AnsiConsole.MarkupLine("• Soft-deleted entries excluded from search but remain for audit trail");
        AnsiConsole.WriteLine();
    }

    /// <summary>
    /// Helper method to construct a LearningEntry with standard defaults.
    /// </summary>
    private static LearningEntry CreateEntry(
        LearningCategory category,
        DecayClass decayClass,
        string content)
    {
        return new LearningEntry
        {
            LearningId = Guid.NewGuid(),
            Category = category,
            DecayClass = decayClass,
            Scope = new LearningScope { IsGlobal = true },
            Content = content,
            Source = new LearningSource
            {
                SourceType = LearningSourceType.ManualEntry,
                SourceId = "example-session",
                SourceDescription = "Example learnings log demonstration"
            },
            Provenance = new LearningProvenance
            {
                OriginPipeline = "learnings_example",
                OriginTask = "manual_entry",
                OriginTimestamp = DateTimeOffset.UtcNow,
                Confidence = 0.95
            },
            FeedbackWeight = 1.0,
            UpdateCount = 0,
            CreatedAt = DateTimeOffset.UtcNow,
            LastAccessedAt = null,
            LastReinforcedAt = null,
            IsDeleted = false,
            DeleteReason = null
        };
    }
}
