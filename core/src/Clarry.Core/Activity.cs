namespace Clarry.Core;

/// <summary>One attribute change an activity declared for one participant.</summary>
public sealed record ActivityEffect(string ActorId, AttributeDelta Delta);

/// <summary>
/// An activity a character proposes inside its own order. This is the shape a language model
/// returns: it never calls the HTTP surface directly, so the ability to invent an activity
/// has to travel as part of an order or it does not exist for the model at all.
/// </summary>
public sealed class ActivityDraft
{
    public string? Name { get; set; }
    public string? Rules { get; set; }
    public string[]? Participants { get; set; }
}

/// <summary>
/// An activity the characters invented themselves -- a drinking contest, a wager, a race,
/// a séance. The world keeps the books and nothing else:
///
///   * the rules are text the host writes,
///   * the moves are free-form strings each participant submits,
///   * the verdict is a declaration somebody makes, not something the world computes.
///
/// That last point is deliberate. A world that interprets arbitrary rules needs an
/// interpreter, and an interpreter is a programming language; a world that only records
/// verdicts needs none, and the characters' own judgement becomes part of the drama. The
/// world checks shape -- that the verdict names a participant, that the effects name real
/// people -- but never checks fairness. A crooked judge is a scene, not a bug.
/// </summary>
public sealed class Activity
{
    public required string Id { get; init; }
    public required string Name { get; init; }

    /// <summary>Who called it together. Conventionally the judge; the world holds no lever.</summary>
    public required string HostId { get; init; }

    /// <summary>The rules, in whatever words the host used. Recorded, never interpreted.</summary>
    public required string Rules { get; init; }

    public List<string> Participants { get; } = new();

    /// <summary>What each participant submitted. Free-form on purpose.</summary>
    public Dictionary<string, string> Moves { get; } = new(StringComparer.Ordinal);

    /// <summary>Who declared the outcome, and what they declared.</summary>
    public string? JudgedBy { get; set; }
    public string? Verdict { get; set; }

    public List<ActivityEffect> Effects { get; } = new();

    public bool Settled { get; set; }
    public DateTimeOffset OpenedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? SettledAt { get; set; }

    public bool EveryoneMoved() =>
        Participants.Count > 0 && Participants.All(id => Moves.ContainsKey(id));
}
