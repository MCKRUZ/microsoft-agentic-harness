using Application.Core.CQRS.Memory;
using FluentValidation.TestHelper;
using Xunit;

namespace Application.Core.Tests.CQRS.Memory;

/// <summary>
/// Validator tests for the memory CQRS surface. The key charset rules are load-bearing: a key is
/// embedded verbatim (lowercased) into the scope-namespaced node id
/// <c>memory:{tenant}:{user}:{key}</c>, so <c>':'</c> (the namespace delimiter), whitespace, and
/// control characters must never pass validation.
/// </summary>
public sealed class MemoryCommandValidationTests
{
    private readonly RememberMemoryCommandValidator _rememberValidator = new();
    private readonly RecallMemoryQueryValidator _recallValidator = new();
    private readonly ForgetMemoryCommandValidator _forgetValidator = new();

    private static RememberMemoryCommand Remember(
        string key = "favorite-color",
        string? content = null,
        string entityType = "Fact") => new()
        {
            Key = key,
            Content = content ?? "The user's favorite color is blue",
            EntityType = entityType
        };

    // == RememberMemoryCommand ==

    [Fact]
    public void Remember_ValidInput_NoErrors()
        => _rememberValidator.TestValidate(Remember()).ShouldNotHaveAnyValidationErrors();

    [Theory]
    [InlineData("")]
    [InlineData("bad:key")]      // ':' is the node-id namespace delimiter
    [InlineData("bad key")]      // whitespace breaks id hygiene
    [InlineData("bad\tkey")]
    [InlineData("-leading-dash")] // must start with a letter or digit
    [InlineData("änderung")]      // outside the ASCII-safe charset
    [InlineData("favorite-color\n")] // #576: trailing newline — '$' matches before it, \A/\z do not
    public void Remember_BadKey_HasError(string key)
        => _rememberValidator.TestValidate(Remember(key: key))
            .ShouldHaveValidationErrorFor(x => x.Key);

    [Fact]
    public void Remember_KeyTooLong_HasError()
        => _rememberValidator.TestValidate(Remember(key: new string('k', MemoryValidationRules.MaxKeyLength + 1)))
            .ShouldHaveValidationErrorFor(x => x.Key);

    [Fact]
    public void Remember_KeyAtMaxLength_NoError()
        => _rememberValidator.TestValidate(Remember(key: new string('k', MemoryValidationRules.MaxKeyLength)))
            .ShouldNotHaveValidationErrorFor(x => x.Key);

    [Fact]
    public void Remember_EmptyContent_HasError()
        => _rememberValidator.TestValidate(Remember(content: ""))
            .ShouldHaveValidationErrorFor(x => x.Content);

    [Fact]
    public void Remember_OversizeContent_HasError()
        => _rememberValidator.TestValidate(Remember(content: new string('c', MemoryValidationRules.MaxContentLength + 1)))
            .ShouldHaveValidationErrorFor(x => x.Content);

    [Fact]
    public void Remember_ContentAtCap_NoError()
        => _rememberValidator.TestValidate(Remember(content: new string('c', MemoryValidationRules.MaxContentLength)))
            .ShouldNotHaveValidationErrorFor(x => x.Content);

    [Theory]
    [InlineData("")]
    [InlineData("1Fact")]        // must start with a letter
    [InlineData("Fact Type")]    // whitespace
    [InlineData("Fact\n")]       // #576: trailing newline — see Remember_BadKey_HasError's identical case
    public void Remember_BadEntityType_HasError(string entityType)
        => _rememberValidator.TestValidate(Remember(entityType: entityType))
            .ShouldHaveValidationErrorFor(x => x.EntityType);

    // == RecallMemoryQuery ==

    [Fact]
    public void Recall_ValidInput_NoErrors()
        => _recallValidator.TestValidate(new RecallMemoryQuery { Query = "color", MaxResults = 5 })
            .ShouldNotHaveAnyValidationErrors();

    [Fact]
    public void Recall_EmptyQuery_HasError()
        => _recallValidator.TestValidate(new RecallMemoryQuery { Query = "" })
            .ShouldHaveValidationErrorFor(x => x.Query);

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(MemoryValidationRules.MaxRecallResults + 1)]
    public void Recall_MaxResultsOutOfRange_HasError(int maxResults)
        => _recallValidator.TestValidate(new RecallMemoryQuery { Query = "q", MaxResults = maxResults })
            .ShouldHaveValidationErrorFor(x => x.MaxResults);

    [Theory]
    [InlineData(1)]
    [InlineData(MemoryValidationRules.MaxRecallResults)]
    public void Recall_MaxResultsAtBounds_NoError(int maxResults)
        => _recallValidator.TestValidate(new RecallMemoryQuery { Query = "q", MaxResults = maxResults })
            .ShouldNotHaveValidationErrorFor(x => x.MaxResults);

    // == ForgetMemoryCommand ==

    [Fact]
    public void Forget_ValidKey_NoErrors()
        => _forgetValidator.TestValidate(new ForgetMemoryCommand { Key = "favorite-color" })
            .ShouldNotHaveAnyValidationErrors();

    [Theory]
    [InlineData("")]
    [InlineData("bad:key")]
    [InlineData("bad key")]
    [InlineData("favorite-color\n")] // #576: trailing newline — see Remember_BadKey_HasError's identical case
    public void Forget_BadKey_HasError(string key)
        => _forgetValidator.TestValidate(new ForgetMemoryCommand { Key = key })
            .ShouldHaveValidationErrorFor(x => x.Key);

    [Fact]
    public void Forget_KeyTooLong_HasError()
        => _forgetValidator.TestValidate(new ForgetMemoryCommand { Key = new string('k', MemoryValidationRules.MaxKeyLength + 1) })
            .ShouldHaveValidationErrorFor(x => x.Key);
}
