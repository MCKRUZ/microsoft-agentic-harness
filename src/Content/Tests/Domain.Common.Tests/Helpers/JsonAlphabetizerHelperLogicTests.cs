using System.Text.Json;
using Domain.Common.Helpers;
using FluentAssertions;
using Xunit;

namespace Domain.Common.Tests.Helpers;

/// <summary>
/// Tests for <see cref="JsonAlphabetizerHelper"/> covering recursive sorting,
/// nested objects, arrays, null values, and error cases.
/// </summary>
public class JsonAlphabetizerHelperLogicTests
{
    [Fact]
    public void AlphabetizeProperties_SimpleObject_SortsAlphabetically()
    {
        var json = """{"zebra": 1, "apple": 2, "mango": 3}""";

        var result = JsonAlphabetizerHelper.AlphabetizeProperties(json);
        using var doc = JsonDocument.Parse(result);
        var props = doc.RootElement.EnumerateObject().Select(p => p.Name).ToList();

        props.Should().ContainInOrder("apple", "mango", "zebra");
    }

    [Fact]
    public void AlphabetizeProperties_NestedObjects_SortsRecursively()
    {
        var json = """{"z": {"b": 1, "a": 2}, "a": {"d": 3, "c": 4}}""";

        var result = JsonAlphabetizerHelper.AlphabetizeProperties(json);
        using var doc = JsonDocument.Parse(result);

        var topProps = doc.RootElement.EnumerateObject().Select(p => p.Name).ToList();
        topProps.Should().ContainInOrder("a", "z");

        var aProps = doc.RootElement.GetProperty("a").EnumerateObject().Select(p => p.Name).ToList();
        aProps.Should().ContainInOrder("c", "d");

        var zProps = doc.RootElement.GetProperty("z").EnumerateObject().Select(p => p.Name).ToList();
        zProps.Should().ContainInOrder("a", "b");
    }

    [Fact]
    public void AlphabetizeProperties_ArrayWithObjects_SortsObjectsInArray()
    {
        var json = """[{"z": 1, "a": 2}, {"m": 3, "b": 4}]""";

        var result = JsonAlphabetizerHelper.AlphabetizeProperties(json);
        using var doc = JsonDocument.Parse(result);
        var firstItem = doc.RootElement[0];
        var props = firstItem.EnumerateObject().Select(p => p.Name).ToList();

        props.Should().ContainInOrder("a", "z");
    }

    [Fact]
    public void AlphabetizeProperties_ArrayWithPrimitives_PreservesOrder()
    {
        var json = """[3, 1, 2]""";

        var result = JsonAlphabetizerHelper.AlphabetizeProperties(json);
        using var doc = JsonDocument.Parse(result);
        var values = doc.RootElement.EnumerateArray().Select(e => e.GetInt32()).ToList();

        values.Should().ContainInOrder(3, 1, 2);
    }

    [Fact]
    public void AlphabetizeProperties_NullValues_PreservesNulls()
    {
        var json = """{"b": null, "a": 1}""";

        var result = JsonAlphabetizerHelper.AlphabetizeProperties(json);
        using var doc = JsonDocument.Parse(result);

        doc.RootElement.GetProperty("b").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public void AlphabetizeProperties_CaseInsensitiveSort()
    {
        var json = """{"Banana": 1, "apple": 2, "Cherry": 3}""";

        var result = JsonAlphabetizerHelper.AlphabetizeProperties(json);
        using var doc = JsonDocument.Parse(result);
        var props = doc.RootElement.EnumerateObject().Select(p => p.Name).ToList();

        props.Should().ContainInOrder("apple", "Banana", "Cherry");
    }

    [Fact]
    public void AlphabetizeProperties_NullInput_ThrowsArgumentException()
    {
        var act = () => JsonAlphabetizerHelper.AlphabetizeProperties(null!);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void AlphabetizeProperties_EmptyInput_ThrowsArgumentException()
    {
        var act = () => JsonAlphabetizerHelper.AlphabetizeProperties("");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void AlphabetizeProperties_WhitespaceInput_ThrowsArgumentException()
    {
        var act = () => JsonAlphabetizerHelper.AlphabetizeProperties("   ");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void AlphabetizeProperties_InvalidJson_ThrowsJsonException()
    {
        var act = () => JsonAlphabetizerHelper.AlphabetizeProperties("{invalid}");

        act.Should().Throw<JsonException>();
    }

    [Fact]
    public void AlphabetizeProperties_OutputIsIndented()
    {
        var json = """{"b": 1, "a": 2}""";

        var result = JsonAlphabetizerHelper.AlphabetizeProperties(json);

        result.Should().Contain("\n");
    }

    [Fact]
    public void AlphabetizeProperties_DeeplyNestedStructure_SortsAllLevels()
    {
        var json = """{"z": {"y": {"x": {"w": 1, "a": 2}}}}""";

        var result = JsonAlphabetizerHelper.AlphabetizeProperties(json);
        using var doc = JsonDocument.Parse(result);
        var innerProps = doc.RootElement
            .GetProperty("z")
            .GetProperty("y")
            .GetProperty("x")
            .EnumerateObject()
            .Select(p => p.Name)
            .ToList();

        innerProps.Should().ContainInOrder("a", "w");
    }
}
