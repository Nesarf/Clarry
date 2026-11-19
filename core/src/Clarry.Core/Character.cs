namespace Clarry.Core;

/// <summary>How a character is driven. Any HTTP-speaking agent can take a seat.</summary>
public enum AgentKind { Human, Llm, Scripted }

/// <summary>
/// Whether this character is a resident of the house or someone who only exists inside a
/// venue. Residents are what the theatre is about: they carry bonds, scenes get picked for
/// them, and they occupy the sixty-four stage seats. Staff exist so that a bar feels staffed;
/// there may be a dozen of them behind one counter and none of them have a life of their own.
///
/// The split exists because the two pressures are different. A scene with twenty speaking
/// residents is unreadable, but a bar with only three staff is empty.
/// </summary>
public enum SeatTier
{
    /// <summary>A resident. Counts against StageLimits.MaxActiveAgents.</summary>
    Principal,

    /// <summary>Venue staff. Does not take a stage seat and is not limited by the stage cap.</summary>
    Supporting
}

/// <summary>How much latitude the agent has. Scripted follows the card, free improvises.</summary>
public enum Autonomy { Scripted, Guided, Free }

/// <summary>What kind of visual asset backs this character, if any.</summary>
public enum VisualKind { None, Pixel, Live2d, Model3d }

/// <summary>
/// The textual card comes first and is always enough to play. Visual assets are added
/// later by the art pipeline (ichiwatashi / lineweight / Live2D / Blender) and never
/// required for a scene to run.
/// </summary>
public sealed class Character
{
    public required string Id { get; init; }
    public required string Name { get; init; }

    /// <summary>True when this seat belongs to the user.</summary>
    public bool IsPlayer { get; init; }

    /// <summary>
    /// Resident or venue staff. Only principals take stage seats; a supporting character
    /// exists inside a venue and never competes for one of the sixty-four.
    /// </summary>
    public SeatTier Tier { get; init; } = SeatTier.Principal;

    public CharacterSheet Sheet { get; init; } = new();
    public CharacterVisual Visual { get; init; } = new();
    public AgentSeat Agent { get; init; } = new();

    /// <summary>
    /// How strongly each trait shows right now, seeded from the sheet and then moved by what
    /// happens to this character. Kept apart from the sheet on purpose: the sheet records what
    /// they were, this records where they currently are.
    /// </summary>
    public TraitMeter Traits { get; set; } = new();

    /// <summary>State right now, not ability. They move with the story, 0..100.</summary>
    public Attributes Attributes { get; } = new();

    /// <summary>
    /// What this character carries. Goes where they go, belongs to them, travels between scenes.
    /// </summary>
    public List<Carried> Carried { get; init; } = new();

    /// <summary>
    /// Buildings this character owns, basic and extended alike. A basic building stands by
    /// itself and brings its own plots -- a workshop has a backyard. An extension stands in one
    /// of those plots, which is why the ground is inside the building rather than beside it.
    /// </summary>
    public List<Building> Buildings { get; init; } = new();

    /// <summary>
    /// Proficiency per craft, keyed by its Chinese name. Traits say who someone is and only
    /// ever shift; this says what they have practised, and doing it is the only way it grows.
    /// </summary>
    public Dictionary<string, int> Skills { get; init; } = new(StringComparer.Ordinal);

    /// <summary>
    /// What others have told this character to do. Kept small and written into the prompt --
    /// advice a model never sees is advice that never happened.
    /// </summary>
    public List<Advice> Advice { get; init; } = new();

    public sealed class CharacterSheet
    {
        public string? Role { get; init; }
        public string? Summary { get; init; }
        public List<string> Traits { get; init; } = new();
        public string? Voice { get; init; }
        public List<string> Goals { get; init; } = new();
        public List<string> Fears { get; init; } = new();
        public List<string> Secrets { get; init; } = new();
        public Dictionary<string, Relation> Relations { get; init; } = new();
    }

    public sealed class Relation
    {
        public string? Kind { get; init; }
        public double Warmth { get; init; }
        public string? Note { get; init; }
    }

    public sealed class CharacterVisual
    {
        public VisualKind Kind { get; init; } = VisualKind.None;
        public string? Ref { get; init; }
        public List<string> Expressions { get; init; } = new();
        public string? Palette { get; init; }
    }

    public sealed class AgentSeat
    {
        public AgentKind Kind { get; init; } = AgentKind.Llm;
        public string? Endpoint { get; init; }
        public string? Model { get; init; }
        public Autonomy Autonomy { get; init; } = Autonomy.Guided;
        public string? Seed { get; init; }
    }
}