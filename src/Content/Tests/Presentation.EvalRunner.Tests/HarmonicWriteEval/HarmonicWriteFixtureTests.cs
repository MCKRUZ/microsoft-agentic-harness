using FluentAssertions;
using Presentation.EvalRunner.HarmonicWriteEval;
using Xunit;

namespace Presentation.EvalRunner.Tests.HarmonicWriteEval;

public sealed class HarmonicWriteFixtureTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"harmonic-fixture-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    private async Task<HarmonicWriteFixture> WriteAndLoadAsync(string json)
    {
        await File.WriteAllTextAsync(_path, json);
        return await HarmonicWriteFixture.LoadAsync(_path, CancellationToken.None);
    }

    [Fact]
    public async Task LoadAsync_ValidFixture_ParsesFactsAndGoldTopicCount()
    {
        var fixture = await WriteAndLoadAsync("""
            {
              "description": "sample",
              "facts": [
                { "key": "a", "content": "fact a", "goldTopic": "t1" },
                { "key": "b", "content": "fact b", "goldTopic": "t1" },
                { "key": "c", "content": "fact c", "goldTopic": "t2" }
              ]
            }
            """);

        fixture.Facts.Should().HaveCount(3);
        fixture.Description.Should().Be("sample");
        fixture.GoldTopicCount.Should().Be(2);
    }

    [Fact]
    public async Task LoadAsync_MissingFile_Throws()
    {
        var act = () => HarmonicWriteFixture.LoadAsync(_path, CancellationToken.None);

        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public async Task LoadAsync_NoFacts_Throws()
    {
        var act = () => WriteAndLoadAsync("""{ "facts": [] }""");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*no facts*");
    }

    [Fact]
    public async Task LoadAsync_DuplicateKey_Throws()
    {
        var act = () => WriteAndLoadAsync("""
            { "facts": [
                { "key": "dup", "content": "x", "goldTopic": "t1" },
                { "key": "dup", "content": "y", "goldTopic": "t2" }
            ] }
            """);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*reuses key*");
    }

    [Fact]
    public async Task LoadAsync_BlankField_Throws()
    {
        var act = () => WriteAndLoadAsync("""
            { "facts": [ { "key": "a", "content": "  ", "goldTopic": "t1" } ] }
            """);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*blank*");
    }
}
