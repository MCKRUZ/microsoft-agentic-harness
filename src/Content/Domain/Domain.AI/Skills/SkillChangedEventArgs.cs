namespace Domain.AI.Skills;

/// <summary>
/// Event arguments for skill file change notifications.
/// </summary>
public class SkillChangedEventArgs : EventArgs
{
	/// <summary>
	/// The skill ID that changed.
	/// </summary>
	public required string SkillId { get; init; }

	/// <summary>
	/// The type of change (Created, Modified, Deleted).
	/// </summary>
	public required WatcherChangeTypes ChangeType { get; init; }

	/// <summary>
	/// The file path that changed.
	/// </summary>
	public required string FilePath { get; init; }
}
