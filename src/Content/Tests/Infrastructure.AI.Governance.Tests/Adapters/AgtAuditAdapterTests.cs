using AgentGovernance.Audit;
using Infrastructure.AI.Governance.Adapters;
using Xunit;

namespace Infrastructure.AI.Governance.Tests.Adapters;

public sealed class AgtAuditAdapterTests
{
    private readonly AgtAuditAdapter _adapter;

    public AgtAuditAdapterTests()
    {
        _adapter = new AgtAuditAdapter(new AuditLogger());
    }

    [Fact]
    public void EntryCount_InitiallyZero()
    {
        Assert.Equal(0, _adapter.EntryCount);
    }

    [Fact]
    public void Log_IncrementsEntryCount()
    {
        _adapter.Log("agent-1", "read_file", "allow");

        Assert.Equal(1, _adapter.EntryCount);
    }

    [Fact]
    public void Log_MultipleEntries_TracksCorrectCount()
    {
        _adapter.Log("agent-1", "read_file", "allow");
        _adapter.Log("agent-1", "write_file", "deny");
        _adapter.Log("agent-2", "execute", "deny");

        Assert.Equal(3, _adapter.EntryCount);
    }

    [Fact]
    public void VerifyChainIntegrity_ValidChain_ReturnsTrue()
    {
        _adapter.Log("agent-1", "read_file", "allow");
        _adapter.Log("agent-1", "write_file", "allow");

        Assert.True(_adapter.VerifyChainIntegrity());
    }

    [Fact]
    public void VerifyChainIntegrity_EmptyChain_ReturnsTrue()
    {
        Assert.True(_adapter.VerifyChainIntegrity());
    }
}
