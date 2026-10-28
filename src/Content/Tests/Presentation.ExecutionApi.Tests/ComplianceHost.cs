using System.Net.Http.Json;
using Application.AI.Common.Interfaces.AI;
using Application.AI.Common.Interfaces.Runs;
using Application.AI.Common.Models.Conversations;
using Domain.AI.Bundles;
using Domain.AI.Runs;
using Domain.Common.Config.AI;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Presentation.ExecutionApi.Tests;

/// <summary>
/// The Execution API host as the compliance tests drive it (#1003): its real composition, with callers told apart by
/// request headers, and a way to seed and read back what a person leaves in this host's own stores.
/// </summary>
internal static class ComplianceHost
{
    /// <summary>The host's name, as every receipt and copy it hands out must state it.</summary>
    public const string Name = "Presentation.ExecutionApi";

    /// <summary>The tenant whose operators may erase and export others in these tests.</summary>
    public const string OperatorTenant = "ops-tenant";

    /// <summary>
    /// The real host with header-minted callers, its operator tenants set to <see cref="OperatorTenant"/> only.
    /// </summary>
    public static WebApplicationFactory<Program> Create() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services
                    .AddAuthentication(HeaderIdentityAuthenticationHandler.SchemeName)
                    .AddScheme<AuthenticationSchemeOptions, HeaderIdentityAuthenticationHandler>(
                        HeaderIdentityAuthenticationHandler.SchemeName, _ => { });
                services.PostConfigure<ErasureConfig>(c => c.OperatorTenantIds = [OperatorTenant]);
            }));

    /// <summary>A request as <paramref name="oid"/>, optionally from <paramref name="tenant"/> and holding <paramref name="roles"/>.</summary>
    public static HttpRequestMessage Post(string path, string oid, object? body = null, string? tenant = null, string? roles = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = body is null ? null : JsonContent.Create(body),
        };
        request.Headers.Add(HeaderIdentityAuthenticationHandler.UserHeader, oid);
        if (tenant is not null)
            request.Headers.Add(HeaderIdentityAuthenticationHandler.TenantHeader, tenant);
        if (roles is not null)
            request.Headers.Add(HeaderIdentityAuthenticationHandler.RolesHeader, roles);
        return request;
    }

    /// <summary>What one person left in this host: a finished workflow run and a conversation with one message.</summary>
    /// <param name="OwnerId">The person.</param>
    /// <param name="RunId">Their finished run.</param>
    /// <param name="ConversationId">Their conversation.</param>
    /// <param name="Message">The text of the message in it, unique to this person.</param>
    public sealed record Seeded(string OwnerId, string RunId, string ConversationId, string Message);

    /// <summary>
    /// Writes a finished workflow run and a conversation for <paramref name="ownerId"/>, the way the host's runs and agents
    /// write them.
    /// </summary>
    public static async Task<Seeded> SeedAsync(IServiceProvider services, string ownerId)
    {
        var label = Guid.NewGuid().ToString("N")[..10];
        var runs = services.GetRequiredService<IRunJobStore>();
        var run = new RunRecord
        {
            JobId = $"run-{label}",
            Kind = RunKind.Workflow,
            TargetId = $"target-{label}",
            OwnerId = ownerId,
            Envelope = new CapabilityEnvelope(),
            Status = RunStatus.Queued,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        runs.TryCreate(run, int.MaxValue).Should().Be(RunAdmission.Accepted);
        // Finished, so erasure may remove it: a live run is kept until it ends (#926).
        runs.Update(run with { Status = RunStatus.Succeeded, CompletedAt = DateTimeOffset.UtcNow }).Should().BeTrue();

        await using var scope = services.CreateAsyncScope();
        var conversations = scope.ServiceProvider.GetRequiredService<IConversationStore>();
        var conversation = await conversations.CreateAsync("agent", ownerId);
        var message = $"message-{label}";
        await conversations.AppendMessageAsync(conversation.Id, ownerId,
            new ConversationMessage(Guid.NewGuid(), MessageRole.User, message, DateTimeOffset.UtcNow));

        return new Seeded(ownerId, run.JobId, conversation.Id, message);
    }

    /// <summary>Whether this host still holds the run and the conversation <paramref name="person"/> left.</summary>
    public static async Task<(bool Run, bool Conversation)> StillHeldAsync(IServiceProvider services, Seeded person)
    {
        var run = services.GetRequiredService<IRunJobStore>().Get(person.RunId, person.OwnerId, null) is not null;
        await using var scope = services.CreateAsyncScope();
        var conversations = await scope.ServiceProvider.GetRequiredService<IConversationStore>().ListAsync(person.OwnerId);
        return (run, conversations.Any(c => c.Id == person.ConversationId));
    }

    /// <summary>A person id no other test uses, so tests sharing the host's on-disk stores never see each other's data.</summary>
    public static string NewPerson(string role) => $"{role}-{Guid.NewGuid():N}";
}
