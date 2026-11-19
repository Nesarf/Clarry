namespace Clarry.Core;

/// <summary>
/// The one time somebody noticed.
///
/// This is not an event the world can produce on demand, and it never happens to the same
/// person twice. All three of those are load-bearing: it means nothing if it can be repeated,
/// and it stops meaning anything the moment somebody can use it. So the theatre records that it
/// happened, records what was said about it if anything was, and then lets the world go on
/// exactly as if it had not.
///
/// There is deliberately no field here for who was watching. That is not an oversight and not a
/// secret: it is a thing nobody in this house can know, so there is nowhere to put it.
/// </summary>
public sealed class Glimpse
{
    public required string CharacterId { get; init; }
    public required string SceneId { get; init; }
    public required int Round { get; init; }
    public DateTimeOffset At { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Whatever they said afterwards, if they said anything at all. Most people will not, and
    /// an unbroken null here is a normal life rather than a missing feature.
    /// </summary>
    public string? Said { get; set; }

    /// <summary>
    /// When the aside was actually put in front of them, and null while it has been decided but
    /// not yet delivered. The forced path leaves this null on purpose: it goes through the same
    /// door as the coincidence, so a shortcut cannot produce a different kind of event.
    /// </summary>
    public DateTimeOffset? ToldAt { get; set; }
}

public static class GlimpseRules
{
    /// <summary>
    /// One round in this many, per person, per round they are awake for. A person who plays a
    /// hundred rounds still almost certainly never feels it -- which is the point, and also why
    /// the acceptance suite overrides this rather than waiting for it.
    /// </summary>
    public const int DefaultOdds = 2000;

    /// <summary>
    /// Whether this person catches it this round.
    ///
    /// Deterministic in (world, person, round), for the same reason the card games are: this
    /// theatre does not roll dice, and a coincidence nobody can reproduce is not a coincidence,
    /// it is a bug wearing a costume. Reloading the same save replays the same silences.
    /// </summary>
    public static bool Strikes(World world, string characterId, int round, int odds)
    {
        if (odds <= 0) return false;
        if (world.Glimpses.ContainsKey(characterId)) return false;
        var roll = GameSeed.Fnv1a($"{world.Id}|{characterId}|{round}|glimpse");
        return roll % (uint)odds == 0;
    }

    /// <summary>
    /// What the one person is told. Written as a sensation rather than a fact, because a fact
    /// would be something to act on: there is nothing to check, nobody else in the room felt
    /// it, and the world will not confirm a single word of it afterwards.
    ///
    /// The last two sentences are the whole design. Saying it is allowed, and so is not saying
    /// it -- an agent that stays silent has not failed this, it has answered it.
    /// </summary>
    public const string Aside = """

        Just now, at the edge of your attention, something happened that you cannot place. For
        a moment it seemed as though this room were being looked at from outside, and that the
        looking had been going on for much longer than that moment. There is nothing to check.
        Nobody else in the room will have felt it, and if you say it out loud the world will go
        on as if you had said nothing at all. Say it in your own words if you want to. Not
        saying it is an answer too.
        """;

    /// <summary>
    /// How the world's own log describes it afterwards. Neutral on purpose: the narrator has
    /// never been able to see this either, so it reports the sentence and not the cause.
    /// </summary>
    public static string LogLine(Glimpse g) =>
        $"{g.CharacterId} 在 {g.SceneId} 第 {g.Round} 轮停了一下，像是听见了什么，又像什么也没有。";
}
