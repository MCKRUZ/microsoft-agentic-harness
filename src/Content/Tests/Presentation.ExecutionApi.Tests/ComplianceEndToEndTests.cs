using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Presentation.Common.Compliance;
using Xunit;
using static Presentation.ExecutionApi.Tests.ComplianceHost;

namespace Presentation.ExecutionApi.Tests;

/// <summary>
/// #1003's done-when, through the Execution API's real composition and HTTP pipeline: a person's runs and conversations
/// in this host's own stores are erased and copied by a request sent to this host, everyone else's are kept, and the
/// receipt and the copy say which host they cover.
/// </summary>
/// <remarks>
/// Before #1003 these endpoints existed on AgentHub only, so a split deployment erased AgentHub's empty copies and handed
/// back a receipt reading Full while this host kept the person's runs and conversations.
/// </remarks>
public sealed class ComplianceEndToEndTests
{
    [Fact]
    public async Task EraseMyData_ErasesTheCallersRunsAndConversationsHere_AndKeepsEveryoneElses()
    {
        using var factory = Create();
        using var client = factory.CreateClient();
        var subject = await SeedAsync(factory.Services, NewPerson("subject"));
        var bystander = await SeedAsync(factory.Services, NewPerson("bystander"));
        (await StillHeldAsync(factory.Services, subject)).Should().Be((true, true), "the seed must be there to erase");

        var response = await client.SendAsync(Post("/api/compliance/erase-my-data", subject.OwnerId));

        var receipt = await JsonOf(response);
        receipt.GetProperty("host").GetString().Should().Be(Name,
            "the receipt speaks for this host only, and must say so");
        Statuses(receipt).Should().NotContain(["Failed", "Skipped"]);
        // The bundle runs this host keeps in memory are on its receipt (#990), and this person's one was removed.
        receipt.GetProperty("stores").EnumerateArray()
            .Should().ContainSingle(s => s.GetProperty("store").GetString() == "bundle runs")
            .Which.GetProperty("status").GetString().Should().Be("Erased");
        factory.Services.GetRequiredService<Application.AI.Common.Interfaces.Bundles.IBundleRunJobStore>()
            .Get(subject.BundleRunId).Should().BeNull();
        factory.Services.GetRequiredService<Application.AI.Common.Interfaces.Bundles.IBundleRunJobStore>()
            .Get(bystander.BundleRunId).Should().NotBeNull("everyone else's bundle runs are kept");
        // The shipped host has no Azure AI Search endpoint: its two lines say so, not "searched, found nothing".
        receipt.GetProperty("stores").EnumerateArray()
            .Where(s => s.GetProperty("store").GetString()!.Contains("Azure AI Search", StringComparison.Ordinal))
            .Select(s => s.GetProperty("status").GetString())
            .Should().Equal("NotConfigured", "NotConfigured");
        (await StillHeldAsync(factory.Services, subject)).Should().Be((false, false));
        (await StillHeldAsync(factory.Services, bystander)).Should().Be((true, true));
    }

    [Fact]
    public async Task EraseMyData_ABodyNamingSomeoneElse_StillErasesOnlyTheCaller()
    {
        // The self-service request has no subject field: whoever signed in is the subject, whatever the body says.
        using var factory = Create();
        using var client = factory.CreateClient();
        var caller = await SeedAsync(factory.Services, NewPerson("mallory"));
        var victim = await SeedAsync(factory.Services, NewPerson("victim"));

        var response = await client.SendAsync(Post(
            "/api/compliance/erase-my-data", caller.OwnerId, new { subjectId = victim.OwnerId, ownerId = victim.OwnerId }));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await StillHeldAsync(factory.Services, victim)).Should().Be((true, true));
        (await StillHeldAsync(factory.Services, caller)).Should().Be((false, false));
    }

    [Fact]
    public async Task EraseSubjectData_AnAllowedOperator_ErasesTheNamedPersonHere_AndKeepsEveryoneElses()
    {
        using var factory = Create();
        using var client = factory.CreateClient();
        var subject = await SeedAsync(factory.Services, NewPerson("subject"));
        var bystander = await SeedAsync(factory.Services, NewPerson("bystander"));
        var operatorSelf = await SeedAsync(factory.Services, NewPerson("dpo"));

        var response = await client.SendAsync(Post(
            "/api/compliance/erasures", operatorSelf.OwnerId, new { subjectId = subject.OwnerId },
            OperatorTenant, ComplianceController.EraseSubjectRole));

        (await JsonOf(response)).GetProperty("host").GetString().Should().Be(Name);
        (await StillHeldAsync(factory.Services, subject)).Should().Be((false, false));
        (await StillHeldAsync(factory.Services, bystander)).Should().Be((true, true));
        (await StillHeldAsync(factory.Services, operatorSelf)).Should().Be((true, true),
            "an operator erasing someone else must not erase themselves");
    }

    [Fact]
    public async Task ExportMyData_CopiesTheCallersRunsAndConversationsHere_AndNoOneElses()
    {
        using var factory = Create();
        using var client = factory.CreateClient();
        var subject = await SeedAsync(factory.Services, NewPerson("subject"));
        var bystander = await SeedAsync(factory.Services, NewPerson("bystander"));

        var response = await client.SendAsync(Post("/api/compliance/export-my-data", subject.OwnerId));

        var body = await BodyOf(response);
        JsonDocument.Parse(body).RootElement.GetProperty("host").GetString().Should().Be(Name,
            "the copy holds only what this host can find, and must say so");
        ManifestStatuses(body).Should().NotContain("Failed", "a store that failed would leave the copy short");
        JsonDocument.Parse(body).RootElement.GetProperty("manifest").EnumerateArray()
            .Where(m => m.GetProperty("store").GetString()!.Contains("Azure AI Search", StringComparison.Ordinal))
            .Should().ContainSingle().Which.GetProperty("status").GetString().Should().Be("NotApplicable");
        body.Should().Contain(subject.Message).And.Contain(subject.RunId).And.Contain(subject.BundleMessage);
        body.Should().NotContain(bystander.Message).And.NotContain(bystander.RunId).And.NotContain(bystander.OwnerId)
            .And.NotContain(bystander.BundleMessage);
        (await StillHeldAsync(factory.Services, subject)).Should().Be((true, true), "a copy deletes nothing");
    }

    [Fact]
    public async Task ExportSubjectData_AnAllowedOperator_CopiesTheNamedPersonsDataHere()
    {
        using var factory = Create();
        using var client = factory.CreateClient();
        var subject = await SeedAsync(factory.Services, NewPerson("subject"));
        var bystander = await SeedAsync(factory.Services, NewPerson("bystander"));

        var response = await client.SendAsync(Post(
            "/api/compliance/exports", NewPerson("dpo"), new { subjectId = subject.OwnerId },
            OperatorTenant, ComplianceExportController.ExportSubjectRole));

        var body = await BodyOf(response);
        JsonDocument.Parse(body).RootElement.GetProperty("host").GetString().Should().Be(Name);
        ManifestStatuses(body).Should().NotContain("Failed");
        body.Should().Contain(subject.Message).And.Contain(subject.RunId);
        body.Should().NotContain(bystander.Message).And.NotContain(bystander.RunId);
    }

    private static async Task<string> BodyOf(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, "the request failed with body: {0}", body);
        return body;
    }

    private static async Task<JsonElement> JsonOf(HttpResponseMessage response) =>
        JsonDocument.Parse(await BodyOf(response)).RootElement;

    private static IEnumerable<string?> ManifestStatuses(string copy) =>
        JsonDocument.Parse(copy).RootElement.GetProperty("manifest").EnumerateArray()
            .Select(m => m.GetProperty("status").GetString()).ToList();

    private static IEnumerable<string?> Statuses(JsonElement receipt) =>
        receipt.GetProperty("stores").EnumerateArray().Select(s => s.GetProperty("status").GetString());
}
