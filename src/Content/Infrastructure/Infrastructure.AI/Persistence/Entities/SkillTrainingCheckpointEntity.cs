namespace Infrastructure.AI.Persistence.Entities;

/// <summary>
/// One stored skill-training checkpoint (#853): the full checkpoint as JSON, plus the columns the store
/// queries on.
/// </summary>
/// <remarks>
/// The payload is the source of truth and is read back whole. The other columns exist only so the store can
/// order and pick without parsing every row: run and step identify the checkpoint, score and step pick the
/// best one.
/// </remarks>
public sealed class SkillTrainingCheckpointEntity
{
    /// <summary>The training run.</summary>
    public required string RunId { get; set; }

    /// <summary>The step within the run; with <see cref="RunId"/> the key.</summary>
    public int Step { get; set; }

    /// <summary>The gate score recorded with the checkpoint, for picking the best.</summary>
    public double Score { get; set; }

    /// <summary>The checkpoint, serialised.</summary>
    public required string PayloadJson { get; set; }
}
