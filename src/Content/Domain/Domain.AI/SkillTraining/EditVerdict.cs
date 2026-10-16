namespace Domain.AI.SkillTraining;

/// <summary>
/// What the gate showed about one tried edit (#845). Derived by the loop from the gate's decision, never
/// written by a model, so an experience entry cannot claim an edit helped when the gate said otherwise.
/// </summary>
public enum EditVerdict
{
    /// <summary>The edit was the only change in its step and the gate accepted the result.</summary>
    Helped = 0,

    /// <summary>The edit was the only change in its step and the result scored below the skill it started from.</summary>
    Hurt = 1,

    /// <summary>The edit was the only change in its step and the result scored no better than the starting skill without being worse.</summary>
    NoObservedEffect = 2,

    /// <summary>
    /// The edit went in together with others, so the result cannot be attributed to it. Preferred over a
    /// guess: a verdict the evidence does not support teaches the wrong lesson.
    /// </summary>
    Confounded = 3
}
