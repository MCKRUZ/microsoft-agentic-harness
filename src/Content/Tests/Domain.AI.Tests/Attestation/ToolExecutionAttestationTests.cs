using Domain.AI.Attestation;
using Xunit;

namespace Domain.AI.Tests.Attestation;

public sealed class ToolExecutionAttestationTests
{
    [Fact]
    public void ToolExecutionAttestation_FailureAttestation_HasNullOutputHash()
    {
        var attestation = new ToolExecutionAttestation
        {
            ToolName = "file_system",
            InputHash = "abc123",
            OutputHash = null,
            Timestamp = DateTimeOffset.UtcNow,
            Signature = "sig",
            KeyVersion = "v1",
            IsFailureAttestation = true,
            FailureReason = "Process crashed"
        };

        Assert.Null(attestation.OutputHash);
        Assert.True(attestation.IsFailureAttestation);
        Assert.Equal("Process crashed", attestation.FailureReason);
    }

    [Fact]
    public void ToolExecutionAttestation_SuccessAttestation_HasBothHashes()
    {
        var attestation = new ToolExecutionAttestation
        {
            ToolName = "file_system",
            InputHash = "abc123",
            OutputHash = "def456",
            Timestamp = DateTimeOffset.UtcNow,
            Signature = "sig",
            KeyVersion = "v1"
        };

        Assert.NotNull(attestation.InputHash);
        Assert.NotNull(attestation.OutputHash);
        Assert.False(attestation.IsFailureAttestation);
    }

    [Fact]
    public void ToolExecutionAttestation_KeyVersion_IsRequired()
    {
        var attestation = new ToolExecutionAttestation
        {
            ToolName = "file_system",
            InputHash = "abc123",
            Timestamp = DateTimeOffset.UtcNow,
            Signature = "sig",
            KeyVersion = "v2"
        };

        Assert.False(string.IsNullOrEmpty(attestation.KeyVersion));
    }
}
