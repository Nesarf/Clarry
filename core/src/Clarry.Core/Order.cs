namespace Clarry.Core;

/// <summary>What a character may do in one round.</summary>
public enum OrderKind
{
    Speak,     // cost 0
    Act,       // cost 1
    Probe,     // cost 1 reserve
    Give,      // cost 1 reserve
    Withhold,  // cost 0, strain +1
    Reveal,    // cost 2
    Divine,    // cost 1, needs the apparatus
    Answer,    // cost 0, answers somebody's open plea
    Plea       // cost 0, asks the world for one specific thing it cannot get alone
}

/// <summary>
/// The Chinese name of each kind. The stage and the round log both read from here, so a
/// player never sees the same action called two different things.
/// </summary>
public static class OrderKindText
{
    public static string Of(OrderKind kind) => kind switch
    {
        OrderKind.Speak => "说话",
        OrderKind.Act => "行动",
        OrderKind.Probe => "试探",
        OrderKind.Give => "给予",
        OrderKind.Withhold => "沉默",
        OrderKind.Reveal => "揭露",
        OrderKind.Divine => "占卜",
        OrderKind.Answer => "应承",
        OrderKind.Plea => "求助",
        _ => kind.ToString()
    };
}

/// <summary>
/// One declared action. It has no effect until the round is settled: that is the whole
/// point of simultaneous turns.
/// </summary>
public sealed class Order
{
    public required string ActorId { get; init; }
    public required OrderKind Kind { get; init; }
    public string? TargetId { get; init; }
    public string? Text { get; init; }
    public int Sequence { get; init; }

    /// <summary>
    /// An activity this order calls into being. Only meaningful on an act; the six-verb
    /// limit is precisely what this field exists to break.
    /// </summary>
    public ActivityDraft? Activity { get; init; }

    /// <summary>
    /// What this character will call the target from now on. Naming someone is itself a move,
    /// so it travels with an order; the model has no other way to reach the naming surface.
    /// </summary>
    public string? Nickname { get; init; }

    /// <summary>
    /// What this character makes of the target, in their own words. An impression travels with
    /// an order for the same reason a nickname does: it is this character's own judgement, and
    /// the model has no other road to it.
    /// </summary>
    public string? Impression { get; init; }

    /// <summary>
    /// Which apparatus a divination uses. Null means "whichever one they hold first" -- a
    /// reader with one tool should not have to name it.
    /// </summary>
    public ApparatusKind? Tool { get; init; }

    /// <summary>
    /// What an astrologer tells the querent to do. Only an unlocked astrologer's word is
    /// recorded, and only once recorded does the listener ever see it.
    /// </summary>
    public string? Advice { get; init; }

    /// <summary>
    /// Which open request this order is answering. Answering is a promise, not a transfer: the
    /// thing asked for still has to be handed over separately, and both are needed to move.
    /// </summary>
    public string? PleaId { get; init; }

    /// <summary>
    /// Where a plea is stuck. Six shapes of wall, and picking one is part of asking: "I need
    /// something" is a mood, "I am short of a thing" is a request somebody can answer.
    /// </summary>
    public Blocked? Blocked { get; init; }

    /// <summary>
    /// What would close the gap, in the asker's own words. The world never parses it; another
    /// character reads it and decides whether they can do that.
    /// </summary>
    public string? Needs { get; init; }

    public int Cost => Kind switch
    {
        OrderKind.Speak => 0,
        OrderKind.Act => 1,
        OrderKind.Probe => 1,
        OrderKind.Give => 1,
        OrderKind.Withhold => 0,
        OrderKind.Reveal => 2,
        OrderKind.Divine => 1,
        OrderKind.Answer => 0,
        OrderKind.Plea => 0,
        _ => 0
    };

    /// <summary>Whether this order draws on reserve (and therefore can fail for lack of it).</summary>
    public bool SpendsReserve => Kind is OrderKind.Probe or OrderKind.Give;

    /// <summary>Whether this order addresses the user's character and needs their word.</summary>
    public bool Addresses(Character target) => TargetId is not null && target.IsPlayer;
}