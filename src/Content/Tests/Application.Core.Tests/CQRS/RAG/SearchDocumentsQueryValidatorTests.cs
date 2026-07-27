using Application.Core.CQRS.RAG.SearchDocuments;
using Domain.Common.Config;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Application.Core.Tests.CQRS.RAG;

/// <summary>
/// Verifies <see cref="SearchDocumentsQueryValidator"/>'s ScopedCollections boundary:
/// caller-supplied collection names are rejected when the feature is on (naming another
/// tenant's collection would be a cross-tenant read primitive) and honored when it is off.
/// </summary>
public sealed class SearchDocumentsQueryValidatorTests
{
    private static SearchDocumentsQueryValidator CreateValidator(bool scopedCollections)
    {
        var appConfig = new AppConfig();
        appConfig.AI.Rag.ScopedCollections.Enabled = scopedCollections;

        var monitor = new Mock<IOptionsMonitor<AppConfig>>();
        monitor.SetupGet(m => m.CurrentValue).Returns(appConfig);
        return new SearchDocumentsQueryValidator(monitor.Object);
    }

    [Fact]
    public void Validate_ScopedCollectionsOnWithCallerSuppliedCollection_Fails()
    {
        var result = CreateValidator(scopedCollections: true)
            .Validate(new SearchDocumentsQuery { Query = "q", CollectionName = "tenant-other-abc" });

        result.IsValid.Should().BeFalse(
            "naming a collection under ScopedCollections would be a cross-tenant read primitive");
        result.Errors.Should().Contain(e =>
            e.PropertyName == nameof(SearchDocumentsQuery.CollectionName));
    }

    [Fact]
    public void Validate_ScopedCollectionsOnWithoutCollection_Passes()
    {
        CreateValidator(scopedCollections: true)
            .Validate(new SearchDocumentsQuery { Query = "q" })
            .IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_ScopedCollectionsOffWithCollection_Passes()
    {
        CreateValidator(scopedCollections: false)
            .Validate(new SearchDocumentsQuery { Query = "q", CollectionName = "corpus-a" })
            .IsValid.Should().BeTrue("flag-off behavior must be unchanged");
    }

    [Fact]
    public void Validate_EmptyQuery_StillFails()
    {
        CreateValidator(scopedCollections: false)
            .Validate(new SearchDocumentsQuery { Query = "" })
            .IsValid.Should().BeFalse("the pre-existing query rules must survive the change");
    }
}
