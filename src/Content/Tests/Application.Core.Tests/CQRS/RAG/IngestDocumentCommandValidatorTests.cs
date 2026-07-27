using Application.Core.CQRS.RAG.IngestDocument;
using Domain.Common.Config;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Application.Core.Tests.CQRS.RAG;

/// <summary>
/// Verifies <see cref="IngestDocumentCommandValidator"/>'s ScopedCollections boundary:
/// caller-supplied collection names are rejected when the feature is on (they would let a
/// caller write into another tenant's collection) and honored when it is off.
/// </summary>
public sealed class IngestDocumentCommandValidatorTests
{
    private static IngestDocumentCommandValidator CreateValidator(bool scopedCollections)
    {
        var appConfig = new AppConfig();
        appConfig.AI.Rag.ScopedCollections.Enabled = scopedCollections;

        var monitor = new Mock<IOptionsMonitor<AppConfig>>();
        monitor.SetupGet(m => m.CurrentValue).Returns(appConfig);
        return new IngestDocumentCommandValidator(monitor.Object);
    }

    private static IngestDocumentCommand Command(string? collectionName) => new()
    {
        DocumentUri = new Uri("file:///docs/report.md"),
        CollectionName = collectionName,
    };

    [Fact]
    public void Validate_ScopedCollectionsOnWithCallerSuppliedCollection_Fails()
    {
        var result = CreateValidator(scopedCollections: true).Validate(Command("corpus-a"));

        result.IsValid.Should().BeFalse(
            "a caller-chosen collection under ScopedCollections is a cross-tenant write primitive");
        result.Errors.Should().Contain(e =>
            e.PropertyName == nameof(IngestDocumentCommand.CollectionName));
    }

    [Fact]
    public void Validate_ScopedCollectionsOnWithoutCollection_Passes()
    {
        CreateValidator(scopedCollections: true).Validate(Command(null))
            .IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_ScopedCollectionsOffWithCollection_Passes()
    {
        CreateValidator(scopedCollections: false).Validate(Command("corpus-a"))
            .IsValid.Should().BeTrue("flag-off behavior must be unchanged");
    }

    [Fact]
    public void Validate_NonFileUri_StillFails()
    {
        var command = new IngestDocumentCommand { DocumentUri = new Uri("https://example.com/doc") };

        CreateValidator(scopedCollections: false).Validate(command)
            .IsValid.Should().BeFalse("the pre-existing file:// scheme rule must survive the change");
    }
}
