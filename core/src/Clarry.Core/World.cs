namespace Clarry.Core;

/// <summary>
/// Dice are not the engine of this theatre. They exist as a policy that is OFF by default
/// and only turned on inside a story when the characters themselves choose to run a
/// tabletop session. Everything else is decided by the agents.
/// </summary>
public sealed class DicePolicy
{
    public bool Enabled { get; set; }
    public string Notation { get; set; } = "1d100";
    public string? Reason { get; set; }
}

/// <summary>One utterance or action in the history.</summary>
public sealed class Turn
{
    public required string ActorId { get; init; }
    public required string Kind { get; init; }   // speech | action | aside | narration
    public required string Text { get; init; }
    public DateTimeOffset At { get; init; } = DateTimeOffset.UtcNow;
    public string? AddressedTo { get; init; }
    public bool NeedsUser { get; init; }
    public string? DiceRoll { get; init; }
}

/// <summary>
/// A world holds everything that outlives a scene: the unbounded registry of characters, the
/// dice policy, the audience's wishes, the activities people invented. Everything that belongs
/// to one stretch of play -- cast, rounds, history, clues -- now lives in a <see cref="Scene"/>.
///
/// There is always at least one scene. A world with none is not a world at rest, it is a bug.
/// </summary>
public sealed class World
{
    public World()
    {
        Scenes.Add(new Scene { Id = "scene-1", Number = 1 });
        SeedPlace();
    }

    /// <summary>
    /// The minimum a world needs before anyone has built anything: one place to leave a request,
    /// and the god said to answer there. Kept in one method because it is used twice -- once when
    /// a world is born, and once when it is wiped back to the same condition. A shrine somebody
    /// raised is a resident's act and does not survive a wipe; this one is the place itself.
    /// </summary>
    private void SeedPlace()
    {
        Deities.Add(ShrineRules.DefaultDeity());
        Shrines.Add(ShrineRules.DefaultShrine());
    }

    public required string Id { get; init; }
    public string Title { get; set; } = "";

    /// <summary>Left empty on purpose: the world's genre is decided by the agents, not by us.</summary>
    public string? Genre { get; set; }
    public string? Premise { get; set; }

    /// <summary>Every character that exists here. No size limit.</summary>
    public Dictionary<string, Character> Registry { get; } = new(StringComparer.Ordinal);

    /// <summary>Every stretch of play, open or closed. Closed scenes stay as record.</summary>
    public List<Scene> Scenes { get; } = new();

    public DicePolicy Dice { get; } = new();

    /// <summary>
    /// The weather over the whole building. It sits above the scenes on purpose: everyone under
    /// one roof is under one sky, and that shared fact is what makes a forecast worth paying
    /// for. It is also the only part of the world an astrologer can change by naming it.
    /// </summary>
    public Sky Sky { get; set; } = new();

    /// <summary>
    /// What each character's face is doing right now, read from the last settled round.
    /// Deliberately not saved: it is the expression of this moment, and a restart has no last
    /// round to have shown. Everyone comes back neutral, which is also what a portrait with no
    /// variants drawn falls back to.
    /// </summary>
    public Dictionary<string, Expression> Faces { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// How each character is being looked at right now: the wide stage, a close-up, or a
    /// remembered frame. Not saved, for the same reason faces are not -- it describes the last
    /// settled round, and a restart has none.
    /// </summary>
    public Dictionary<string, Shot> Shots { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// The scene currently taking play. When every scene has closed, a fresh one opens rather
    /// than the world sitting there with nowhere for anyone to stand.
    /// </summary>
    public Scene Active
    {
        get
        {
            var open = Scenes.FirstOrDefault(s => s.Open);
            if (open is not null) return open;

            var next = new Scene { Id = NextSceneId(), Number = Scenes.Count + 1 };
            Scenes.Add(next);
            return next;
        }
    }

    /// <summary>
    /// How many residents are busy across every open scene. This is what the sixty-four actually
    /// counts -- not one scene's cast, and not the registry. Three scenes holding thirty people
    /// each already breaks the limit; a scene holding sixty-four does not.
    /// </summary>
    public int OccupiedCount => Scenes.Where(s => s.Open).Sum(s => s.Cast.Count);

    /// <summary>Room left before the global cap, across all open scenes together.</summary>
    public int RoomLeft => Math.Max(0, StageLimits.MaxActiveAgents - OccupiedCount);

    // --- the compatibility surface -------------------------------------------------------
    // Everything below reads and writes the active scene. It exists so that code written when
    // there was only ever one scene keeps working unchanged; new code should talk to a Scene.

    public Stage Stage => Active.Cast;
    public List<Turn> History => Active.History;
    public Round? CurrentRound => Active.CurrentRound;
    public List<Round> Rounds => Active.Rounds;

    public int OrdersPerActor
    {
        get => Active.OrdersPerActor;
        set => Active.OrdersPerActor = value;
    }

    public int CluesResolved
    {
        get => Active.CluesResolved;
        set => Active.CluesResolved = value;
    }

    public int ClueGoal
    {
        get => Active.ClueGoal;
        set => Active.ClueGoal = value;
    }

    public int TurnPointer
    {
        get => Active.TurnPointer;
        set => Active.TurnPointer = value;
    }

    // --- wishes and activities live on the world, not the scene --------------------------

    /// <summary>
    /// The audience's wishes for this scene. Two is the whole budget now, not three: the list
    /// lost three of its four interventions, and what is left is heavier than what went away.
    /// Answering somebody's request is worth more than arranging an encounter ever was.
    /// </summary>
    public int WishBudget { get; set; } = 2;
    public int WishesLeft { get; set; } = 2;

    /// <summary>What the audience already asked for, so it can see what actually changed.</summary>
    public List<Wish> Wishes { get; } = new();

    /// <summary>
    /// A forced encounter: these two share the next scene, warmth or not. No wish writes this
    /// any more -- the audience no longer decides who meets whom -- but the picker still honours
    /// it, because a script or a test may want to stage something specific.
    /// </summary>
    public string? ForcedPairA { get; set; }
    public string? ForcedPairB { get; set; }
    /// <summary>
    /// Activities the characters invented for themselves. The world records the rules, the
    /// moves and the verdict; it never interprets any of them. This is the extension point
    /// that keeps the theatre from being limited to six verbs.
    /// </summary>
    public List<Activity> Activities { get; } = new();

    /// <summary>
    /// The places people run: cafes, bars, shops. They belong to the world rather than to a
    /// scene, because a shop does not move when the camera does -- the people walk in and out
    /// of it, and it is still there tomorrow.
    /// </summary>
    public List<Venue> Venues { get; } = new();

    /// <summary>
    /// Things people have asked the world for and are still waiting on. A request outlives the
    /// round it was made in and is not tied to one scene, so it lives here rather than on a Scene.
    /// </summary>
    public List<Plea> Pleas { get; } = new();

    /// <summary>Monotonic, so a request keeps its id even after older ones are dropped.</summary>
    public int NextPleaNumber { get; set; } = 1;

    /// <summary>Places where a request can be left. Most worlds need one.</summary>
    public List<Shrine> Shrines { get; } = new();

    /// <summary>Whatever is said to answer at those places. Nobody has ever seen one.</summary>
    public List<Deity> Deities { get; } = new();

    // --- scenes ---------------------------------------------------------------------------

    public string NextSceneId() => "scene-" + (Scenes.Count + 1);

    /// <summary>
    /// Opens a scene with the given cast. Refuses when the people are already busy elsewhere or
    /// when the global cap has no room -- a resident can only be in one place at a time.
    /// </summary>
    public (Scene? Scene, string? Reason) OpenScene(IEnumerable<string> cast, string title = "")
    {
        var wanted = cast.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToList();
        if (wanted.Count == 0) return (null, "一幕总得有人");

        foreach (var id in wanted)
            if (Find(id) is null) return (null, "没有这个角色：" + id);

        var busy = Scenes.Where(s => s.Open)
                         .SelectMany(s => s.Cast.Active)
                         .ToHashSet(StringComparer.Ordinal);
        foreach (var id in wanted)
            if (busy.Contains(id)) return (null, id + " 已经在另一幕里了");

        if (OccupiedCount + wanted.Count > StageLimits.MaxActiveAgents)
            return (null, $"台上已经满了：还能再进 {RoomLeft} 位");

        var scene = new Scene { Id = NextSceneId(), Number = Scenes.Count + 1, Title = title };
        foreach (var id in wanted) scene.Cast.Enter(id);
        Scenes.Add(scene);
        return (scene, null);
    }

    /// <summary>Closes a scene. The people stay registered, and stay free for another scene.</summary>
    public bool CloseScene(string sceneId, DateTimeOffset? at = null)
    {
        var scene = Scenes.FirstOrDefault(s => s.Id == sceneId);
        if (scene is null || !scene.Open) return false;
        scene.Close(at);
        return true;
    }

    /// <summary>Open scenes other than the active one -- what the audience could switch to.</summary>
    public List<Scene> Parallel() => Scenes.Where(s => s.Open).Skip(1).ToList();

    // --- the audience's camera -------------------------------------------------------------

    /// <summary>
    /// A scene the audience has pinned the camera to. Null means "follow whatever moved last",
    /// which is the default on purpose: an audience should not have to keep choosing.
    /// </summary>
    public string? FocusScene { get; set; }

    /// <summary>
    /// Where the camera is right now. A pinned scene that has since closed is not a scene any
    /// more, so the camera falls back to following rather than showing an empty room to nobody.
    /// </summary>
    public Scene? Focus()
    {
        var open = Scenes.Where(s => s.Open).ToList();
        if (open.Count == 0) return Scenes.LastOrDefault();

        if (FocusScene is not null && open.FirstOrDefault(s => s.Id == FocusScene) is { } pinned)
            return pinned;

        return open.OrderByDescending(s => s.LastChangedAt).ThenBy(s => s.Number).First();
    }

    /// <summary>Why the camera is where it is, said in words rather than left to be inferred.</summary>
    public string FocusBecause()
    {
        var focused = Focus();
        if (focused is null) return "没有开着的幕";
        if (FocusScene is not null && focused.Id == FocusScene) return $"{focused.Id} 是钉住的";
        if (!focused.Open) return $"{focused.Id} 已经收了";
        return $"{focused.Id} 最近动过";
    }

    // --- characters -----------------------------------------------------------------------

    public Character Add(Character character)
    {
        ArgumentNullException.ThrowIfNull(character);
        // Declared traits arrive at full strength; afterwards only events move them.
        character.Traits.FromSheet(character.Sheet.Traits);
        Registry[character.Id] = character;
        return character;
    }

    public Character? Find(string id) => Registry.GetValueOrDefault(id);

    public Round BeginRound() => Active.BeginRound();

    /// <summary>Who speaks next, or null when the active scene is empty.</summary>
    public Character? CurrentActor() => Active.CurrentActor(this);

    public Turn Record(string actorId, string kind, string text, string? to = null) =>
        Active.Record(actorId, kind, text, to);

    /// <summary>
    /// Starts the active scene over: its history, round ledger and clue progress are cleared,
    /// the cast stays. The world's wishes and activities belong to the whole run and are reset
    /// too, because a new scene is a new occasion for the audience.
    /// </summary>
    public void ResetScene(int? clueGoal = null)
    {
        Active.Rewind(clueGoal);
        WishesLeft = WishBudget;
        Wishes.Clear();
        Activities.Clear();
        ForcedPairA = null;
        ForcedPairB = null;
        // A new play starts on no camera in particular. Leaving a pin behind would point the
        // audience at a scene that no longer exists and call it a decision.
        FocusScene = null;
    }

    /// <summary>
    /// Takes the world back to before anyone arrived: nobody registered, no history, no requests
    /// waiting on an answer, no shops, no wishes spent. What stays is the place itself -- the sky
    /// above it, the name it was given, and the one step in the courtyard with the god over it --
    /// because none of those are things a resident did, while a shrine somebody raised is.
    ///
    /// The scene list is emptied and then given one fresh scene rather than left empty. A world
    /// with no scene is not a world at rest, it is a bug, and <see cref="Active"/> would quietly
    /// create one on the next read anyway -- with a number derived from a list that says zero
    /// while the file says five hundred.
    ///
    /// Faces and shots go with the people: they describe the last settled round, and there is
    /// no last round to describe.
    /// </summary>
    public void Wipe()
    {
        Registry.Clear();
        Faces.Clear();
        Shots.Clear();

        Scenes.Clear();
        Scenes.Add(new Scene { Id = NextSceneId(), Number = 1 });

        Pleas.Clear();
        NextPleaNumber = 1;
        Wishes.Clear();
        WishesLeft = WishBudget;
        Activities.Clear();
        Venues.Clear();

        // The place goes back to how it was found: one step in the courtyard and the god over
        // it. Sixteen shrines with nobody who built them reads as history that never happened.
        Shrines.Clear();
        Deities.Clear();
        SeedPlace();

        ForcedPairA = null;
        ForcedPairB = null;
        FocusScene = null;

        // Dice are a policy the characters switch on for themselves. Nobody has done anything
        // here yet, so there is nothing to have chosen them.
        Dice.Enabled = false;
    }
}
