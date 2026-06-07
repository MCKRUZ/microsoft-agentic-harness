using Domain.AI.Identity;

namespace Infrastructure.AI.Tests.Egress.Support;

internal static class TestIdentity
{
    public static AgentIdentity Default { get; } = new()
    {
        Id = "agent-egress",
        Kind = AgentIdentityKind.Development,
        TenantId = "tenant-egress"
    };
}
