namespace Clarry.Core;

/// <summary>
/// What a face is doing right now. Five states rather than a continuum, because a portrait
/// pipeline has to draw a finite number of files: a character who is "sixty-three percent
/// pleased" cannot be drawn, and a system that pretends it can will always show a neutral face.
///
/// The face is read out of a settled round, deterministically. The same settlement produces the
/// same expression, so a replayed evening shows the same faces -- which matters more than it
/// sounds: an audience that rewinds should not see a different mood.
/// </summary>
public enum Expression
{
    /// <summary>Nothing in particular. Also the fallback when a portrait has no variants drawn.</summary>
    Neutral,

    /// <summary>Gave something away, or said a true thing out loud.</summary>
    Happy,

    /// <summary>Held back on purpose, or had a declaration thrown away for lack of points.</summary>
    Unhappy,

    /// <summary>Someone saw through them, or they lost a name they were holding.</summary>
    Surprised,

    /// <summary>Carrying an ember into this round. Outranks everything: fire is the loudest face.</summary>
    Ember
}

public static class ExpressionRules
{
    public static string Text(Expression e) => e switch
    {
        Expression.Neutral => "平常",
        Expression.Happy => "舒展",
        Expression.Unhappy => "沉下来",
        Expression.Surprised => "被点到了",
        Expression.Ember => "带火",
        _ => e.ToString()
    };

    /// <summary>
    /// Reads one face out of a settled round. The order is the whole rule, so it is worth
    /// stating: an ember outshines everything, being seen through outshines what you meant to
    /// do, and your own declaration decides the rest. Neutral is an answer, not a failure.
    /// </summary>
    public static Expression Of(string id, Round round, RoundResult result, Character who)
    {
        ArgumentNullException.ThrowIfNull(round);
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(who);

        if (who.Attributes.Ember) return Expression.Ember;

        var mine = round.Orders.Where(o => o.ActorId == id).ToList();
        var saidTruth = mine.Any(o => o.Kind == OrderKind.Reveal);

        // Being named at the end of a reveal line while not having revealed anything yourself
        // means somebody else put a light on you. It is the one thing on this list you did not
        // choose. The prober's own name sits at the start of that same line, so position is
        // what tells the two apart -- a substring test would startle both of them.
        if (!saidTruth && result.Revealed.Any(line => PointedAt(line, id)))
            return Expression.Surprised;

        if (saidTruth) return Expression.Happy;
        if (mine.Any(o => o.Kind == OrderKind.Give)) return Expression.Happy;
        if (mine.Any(o => o.Kind == OrderKind.Withhold)) return Expression.Unhappy;
        if (result.Ignored.Any(o => o.ActorId == id)) return Expression.Unhappy;

        return Expression.Neutral;
    }

    /// <summary>
    /// Whether a log line puts its light on this character, which is to say whether their id is
    /// the last word of it. "alice 看穿了 bob" points at bob, not at alice. Shared with the
    /// shot rules, which cut to a close-up for the same reason.
    /// </summary>
    internal static bool PointedAt(string line, string id) =>
        line.EndsWith(" " + id, StringComparison.Ordinal);
}
