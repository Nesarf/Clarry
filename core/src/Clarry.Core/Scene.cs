namespace Clarry.Core;

/// <summary>
/// One stretch of play: a handful of characters, in one place, for as long as it holds.
///
/// A scene owns everything that used to belong to "the world" -- its cast, its round ledger,
/// its own history, its own clue progress, its own convergence. The world above it owns the
/// people and the relationships, which outlive any single scene.
///
/// This distinction is what lets two things happen at once that could not before: one group
/// can be arguing in a bar while another is climbing something, and neither has to wait for
/// the other to finish a round.
/// </summary>
public sealed class Scene
{
    public required string Id { get; init; }
    public required int Number { get; init; }
    public string Title { get; set; } = "";

    /// <summary>Who is in this scene. Capacity is per-scene; the global cap lives on World.</summary>
    public Stage Cast { get; } = new();

    public List<Turn> History { get; } = new();

    public Round? CurrentRound { get; private set; }
    public List<Round> Rounds { get; } = new();

    public int OrdersPerActor { get; set; } = 2;
    public int CluesResolved { get; set; }
    public int ClueGoal { get; set; } = 3;
    public int TurnPointer { get; set; }

    /// <summary>False once the scene has converged or been called off.</summary>
    public bool Open { get; set; } = true;

    public DateTimeOffset OpenedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ClosedAt { get; set; }

    /// <summary>
    /// When something last happened here. The audience's camera follows whichever scene moved
    /// most recently, so this is the clock that behaviour is built on -- and it is an event
    /// time, not the wall clock passing. A scene nobody is doing anything in does not age.
    /// </summary>
    public DateTimeOffset LastChangedAt { get; private set; } = DateTimeOffset.UtcNow;

    public void Touch(DateTimeOffset? at = null) => LastChangedAt = at ?? DateTimeOffset.UtcNow;

    /// <summary>How long this scene has run, in rounds.</summary>
    public int Length => Rounds.Count;

    public Round BeginRound()
    {
        if (CurrentRound is { Closed: false })
            throw new InvalidOperationException("这一幕上一轮还没有结束");
        var next = new Round { Number = Rounds.Count + 1 };
        Rounds.Add(next);
        CurrentRound = next;
        return next;
    }

    public Turn Record(string actorId, string kind, string text, string? to = null)
    {
        var turn = new Turn { ActorId = actorId, Kind = kind, Text = text, AddressedTo = to };
        History.Add(turn);
        // A line of dialogue is the thing the audience came to see, so it is also what moves
        // the camera. Anything that lands in the history lands here.
        Touch(turn.At);
        return turn;
    }

    /// <summary>Who speaks next within this scene, or null when it is empty.</summary>
    public Character? CurrentActor(World world)
    {
        if (Cast.Count == 0) return null;
        var id = Cast.Active[TurnPointer % Cast.Count];
        return world.Find(id);
    }

    public void Close(DateTimeOffset? at = null)
    {
        Open = false;
        ClosedAt = at ?? DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// A scene starts over when its clue tree fills: same people, new stretch. Nothing about
    /// them resets -- they keep the wounds and the memories.
    /// </summary>
    public void Rewind(int? clueGoal = null)
    {
        History.Clear();
        Rounds.Clear();
        CurrentRound = null;
        CluesResolved = 0;
        TurnPointer = 0;
        Open = true;
        ClosedAt = null;
        if (clueGoal is > 0) ClueGoal = clueGoal.Value;
    }
}
