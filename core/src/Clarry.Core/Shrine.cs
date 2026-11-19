namespace Clarry.Core;

/// <summary>
/// Something that answers, as far as anyone in this house knows. It has a name, a domain and a
/// habit of answering sometimes. It never speaks, nobody has seen it, and it keeps no record of
/// who was watching when it did.
///
/// This class exists so that the fourth wall cannot be broken by accident. A granted request is
/// never recorded as "the audience did this" -- it is recorded as this god answering. The
/// characters therefore have a word for what happens to them, and that word is not "player".
/// </summary>
public sealed class Deity
{
    public required string Id { get; init; }
    public required string Name { get; init; }

    /// <summary>What it is said to look after. Flavour, and a reason to choose one shrine over another.</summary>
    public required string Domain { get; init; }

    public string Temper { get; init; } = "沉默";

    public int Asked { get; set; }
    public int Answered { get; set; }
}

/// <summary>
/// A place where a request can be left. It stands somewhere, and the requests written here are
/// addressed to the god over it rather than to the sky in general.
/// </summary>
public sealed class Shrine
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string DeityId { get; init; }

    /// <summary>Set when the shrine belongs to a venue -- a step outside a shop, a shelf in a bar.</summary>
    public string? VenueId { get; init; }

    public DateTimeOffset RaisedAt { get; init; } = DateTimeOffset.UtcNow;
}

public static class ShrineRules
{
    /// <summary>The step in the courtyard a new world comes with, so a world is usable at once.</summary>
    public static Deity DefaultDeity() => new()
    {
        Id = "deity-1",
        Name = "雾中那位",
        Domain = "归途与失物",
        Temper = "不常应，应了就准"
    };

    public static Shrine DefaultShrine() => new()
    {
        Id = "shrine-1",
        Name = "庭前石阶",
        DeityId = "deity-1"
    };

    public static (Shrine? Shrine, string? Reason) Raise(World world, string name, Deity deity, string? venueId = null)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(deity);

        if (string.IsNullOrWhiteSpace(name)) return (null, "一处地方总得有个叫法");
        if (world.Shrines.Any(s => string.Equals(s.Name, name.Trim(), StringComparison.Ordinal)))
            return (null, "已经有一处叫「" + name.Trim() + "」的地方了");

        if (world.Deities.All(d => d.Id != deity.Id)) world.Deities.Add(deity);

        var shrine = new Shrine
        {
            Id = "shrine-" + (world.Shrines.Count + 1),
            Name = name.Trim(),
            DeityId = deity.Id,
            VenueId = venueId
        };
        world.Shrines.Add(shrine);
        return (shrine, null);
    }

    /// <summary>Which shrine a request was left at. Named, or the first one standing.</summary>
    public static Shrine? Pick(World world, string? shrineId)
    {
        if (world.Shrines.Count == 0) return null;
        if (string.IsNullOrWhiteSpace(shrineId)) return world.Shrines[0];
        return world.Shrines.FirstOrDefault(s => s.Id == shrineId);
    }

    public static Deity? DeityOf(World world, Shrine shrine) =>
        world.Deities.FirstOrDefault(d => d.Id == shrine.DeityId);

    /// <summary>
    /// What the world says afterwards. Written as a thing that turned up and a rumour about why,
    /// never as somebody deciding to be kind -- an audience watching is not a fact anyone here
    /// can have. The "some say" is doing real work: it keeps the god a rumour rather than a
    /// mechanic, so a person can doubt it.
    /// </summary>
    public static string AnsweredLine(Plea plea, Shrine shrine, Deity deity) =>
        $"第二天，{plea.Needs}出现在了{plea.FromId}手边。{shrine.Name}前那张纸不见了——有人说那是{deity.Name}应的。";
}
