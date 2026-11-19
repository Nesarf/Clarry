namespace Clarry.Core;

/// <summary>
/// What a character is short of, when it cannot take its next step. Six shapes of wall, no more:
/// the point of this enum is to make "stuck" into something an audience and a neighbour can both
/// read at a glance, not to model every possible obstruction.
/// </summary>
public enum Blocked
{
    Object,
    Skill,
    Person,
    Place,
    Permit,
    Money
}

public static class BlockedText
{
    public static string Of(Blocked kind) => kind switch
    {
        Blocked.Object => "缺一件东西",
        Blocked.Skill => "缺一样本事",
        Blocked.Person => "缺一个人",
        Blocked.Place => "缺一处地方",
        Blocked.Permit => "缺一个许可",
        Blocked.Money => "缺钱",
        _ => kind.ToString()
    };

    public static bool TryParse(string? text, out Blocked kind) =>
        Enum.TryParse(text, ignoreCase: true, out kind);
}

public enum PleaState
{
    Open,

    /// <summary>Somebody answered. Which of the two answering roads this was is in AnsweredBy.</summary>
    Answered,

    /// <summary>Nobody answered in time. The character goes another way, and that is not a loss.</summary>
    Lapsed
}

/// <summary>
/// A character asking the world for one specific thing it cannot get by itself.
///
/// This is the load-bearing idea behind the whole interaction layer: because a resident has real
/// autonomy, the story must be able to finish without anyone watching. So a plea has three exits
/// and only one of them needs a person at the other end:
///
///   - another character answers it        -- the social road, and the most interesting one
///   - the shrine answers it               -- the audience road, entirely optional
///   - nobody answers, it lapses           -- the autonomous road, and often the best scene
///
/// "Lapsed" is a verb here, not a failure. Someone asked, nobody came, and they found another way
/// -- that changes a person more than being helped does.
/// </summary>
public sealed class Plea
{
    public required string Id { get; init; }
    public required string FromId { get; init; }

    /// <summary>Named recipient, or null for "anyone who hears it".</summary>
    public string? ToId { get; init; }

    /// <summary>
    /// Where it was left. A request with a shrine is addressed to the god over that shrine; a
    /// request without one is only ever heard by other people.
    /// </summary>
    public string? ShrineId { get; init; }

    public required Blocked Blocked { get; init; }

    /// <summary>What is asked for, in the asker's own words. The world never parses this.</summary>
    public required string Needs { get; init; }

    /// <summary>How they asked. Kept separate from Needs so a plain request and a plea sound differ.</summary>
    public required string Text { get; init; }

    /// <summary>How many more rounds this stays open. Counted in rounds, never in wall time.</summary>
    public int Lifetime { get; set; }

    public string? AnsweredBy { get; set; }
    public PleaState State { get; set; } = PleaState.Open;
    public DateTimeOffset At { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Opening, answering and outliving requests. Nothing here decides what an answer is worth --
/// it records who asked, who answered, and who did not, and lets the round settle the rest.
/// </summary>
public static class Pleas
{
    /// <summary>Three rounds is long enough to be noticed and short enough to hurt.</summary>
    public const int DefaultLifetime = 3;

    /// <summary>How many lapsed requests are kept as record before the oldest are dropped.</summary>
    public const int KeepLapsed = 20;

    public static Plea Open(World world, Character who, Blocked kind, string needs, string text,
                            string? toId = null, int lifetime = DefaultLifetime, string? shrineId = null)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(who);

        var shrine = ShrineRules.Pick(world, shrineId);
        if (shrine is not null) ShrineRules.DeityOf(world, shrine)!.Asked++;

        var plea = new Plea
        {
            Id = "plea-" + world.NextPleaNumber++,
            FromId = who.Id,
            ToId = toId,
            ShrineId = shrine?.Id,
            Blocked = kind,
            Needs = needs.Trim(),
            Text = string.IsNullOrWhiteSpace(text) ? $"{who.Id} 开口求了一件事" : text.Trim(),
            Lifetime = Math.Max(1, lifetime)
        };
        world.Pleas.Add(plea);
        return plea;
    }

    /// <summary>
    /// Someone answers. Whether they actually hand over what was asked for is a separate question,
    /// settled by whatever they then do -- answering is a promise, not a transfer.
    /// </summary>
    public static (Plea? Plea, string? Reason) Answer(World world, string pleaId, Character by)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(by);

        var plea = world.Pleas.FirstOrDefault(p => p.Id == pleaId);
        if (plea is null) return (null, "没有这条请求：" + pleaId);
        if (plea.FromId == by.Id) return (null, "自己应不了自己的请求");
        if (plea.ToId is not null && plea.ToId != by.Id)
            return (null, $"这条请求是指名给 {plea.ToId} 的");

        switch (plea.State)
        {
            case PleaState.Answered:
                return (null, $"这条请求已经有人应了：{plea.AnsweredBy}");
            case PleaState.Lapsed:
                return (null, "这条请求已经过期了");
        }

        plea.AnsweredBy = by.Id;
        plea.State = PleaState.Answered;
        return (plea, null);
    }

    /// <summary>
    /// Ages every open request by one round. Returns how many lapsed this time -- a number worth
    /// putting in the log, because "nobody came" is a thing that happened to somebody.
    /// </summary>
    public static int Expire(World world)
    {
        ArgumentNullException.ThrowIfNull(world);

        var lapsed = 0;
        foreach (var plea in world.Pleas.Where(p => p.State == PleaState.Open))
        {
            plea.Lifetime--;
            if (plea.Lifetime > 0) continue;
            plea.State = PleaState.Lapsed;
            lapsed++;
        }

        // Lapsed requests stay as record for a while; once the "nobody came" has been read,
        // keeping them forever would only make the list unreadable.
        var dead = world.Pleas.Where(p => p.State == PleaState.Lapsed).ToList();
        if (dead.Count > KeepLapsed)
            foreach (var old in dead.Take(dead.Count - KeepLapsed))
                world.Pleas.Remove(old);

        return lapsed;
    }

    public static List<Plea> OpenFor(World world, string selfId) =>
        world.Pleas
            .Where(p => p.State == PleaState.Open)
            .Where(p => p.FromId != selfId)
            .Where(p => p.ToId is null || p.ToId == selfId)
            .ToList();

    public static List<Plea> By(World world, string selfId) =>
        world.Pleas.Where(p => p.FromId == selfId).ToList();
}
