namespace Clarry.Core;

/// <summary>
/// Six numbers describing a character's state right now -- not their ability.
/// They move with the story, between 0 and 100.
/// </summary>
public sealed class Attributes
{
    public int Poise { get; set; } = 50;     // composure and command of the scene
    public int Insight { get; set; } = 50;   // seeing through intent and clues
    public int Renown { get; set; } = 50;    // weight in others' eyes
    public int Strain { get; set; } = 20;    // unresolved pressure
    public int Reserve { get; set; } = 50;   // time, money, favours still available

    /// <summary>
    /// Ember, in the Dark Souls III sense: a state carried into the next encounter rather
    /// than a stat accumulated. Saying a true thing out loud lights you; while it burns, a
    /// bold move lands harder, and the price is paid in strain. It never stacks and goes out
    /// after one use -- the point is that it is spent, not kept.
    /// </summary>
    public bool Ember { get; set; }

    /// <summary>Bond is the only per-target attribute: there is no single score for trust.</summary>
    public Dictionary<string, int> Bond { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// What this character calls someone else -- a nickname, a title, a diminishment. Life
    /// sims run on this: the moment A starts calling B something only A calls them, the two
    /// of them read as close, and no amount of numbers produces that effect. It has to be
    /// written into the prompt as well, or the model will never use it.
    /// </summary>
    public Dictionary<string, string> Names { get; } = new(StringComparer.Ordinal);

    public string NameFor(string id) =>
        Names.TryGetValue(id, out var name) && !string.IsNullOrWhiteSpace(name) ? name : id;

    public int BondWith(string id) => Bond.TryGetValue(id, out var v) ? v : 0;

    public void ShiftBond(string id, int delta)
    {
        var next = Math.Clamp(BondWith(id) + delta, 0, 100);
        Bond[id] = next;
    }

    public static int Clamp(int v) => Math.Clamp(v, 0, 100);
}

/// <summary>The six numbers a settlement may change, reported as a diff.</summary>
public sealed record AttributeDelta(int Poise, int Insight, int Renown, int Strain, int Reserve, bool Ember = false)
{
    public static readonly AttributeDelta Zero = new(0, 0, 0, 0, 0);
}