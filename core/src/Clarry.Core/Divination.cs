namespace Clarry.Core;

/// <summary>
/// The four things an astrologer needs, named the way a person would name them. This is the
/// vocabulary the prompt and the HTTP surface use; where each one actually lives is a separate
/// question, and <see cref="ApparatusRules"/> answers it.
/// </summary>
public enum ApparatusKind
{
    /// <summary>塔罗牌 -- carried, reads a person.</summary>
    Tarot,

    /// <summary>预言水晶 -- carried, sees into a room you are not standing in.</summary>
    Crystal,

    /// <summary>炼金工房 -- a basic building, turns the hour into something you can carry.</summary>
    Alembic,

    /// <summary>天文台 -- an extension, reads the sky and thereby names the weather.</summary>
    Observatory
}

/// <summary>
/// How a thing is held. Four words, three levels underneath them, and the difference is real
/// rather than nominal.
///
/// A carried thing goes where its owner goes. A basic building is a building: it stands by
/// itself and comes with its own ground -- a workshop has a backyard. That backyard is a plot,
/// and a plot belongs to the building it is part of; it is not a lot you buy separately and it
/// is not a building you can enter as one. An extension has no ground at all: it is added to a
/// plot that some basic building already provides, which is why nobody can own an observatory
/// without first owning the workshop whose backyard it stands in.
/// </summary>
public enum Housing
{
    /// <summary>Carried: moves with the person.</summary>
    Carried,

    /// <summary>A plot inside a basic building. Holds extensions; is not itself a building.</summary>
    Ground,

    /// <summary>Basic: stands by itself and brings its own plots.</summary>
    Basic,

    /// <summary>Extension: stands on a plot, never on the ground directly.</summary>
    Extension
}

public static class ApparatusText
{
    public static string Of(ApparatusKind kind) => kind switch
    {
        ApparatusKind.Tarot => "塔罗牌",
        ApparatusKind.Crystal => "预言水晶",
        ApparatusKind.Alembic => "炼金工房",
        ApparatusKind.Observatory => "天文台",
        _ => kind.ToString()
    };

    public static bool TryParse(string? text, out ApparatusKind kind) =>
        Enum.TryParse(text, ignoreCase: true, out kind);
}

/// <summary>
/// What a plot inside a building can be. Ground is not one thing: a workshop has a backyard, a
/// shop has a counter and somewhere for people to sit, and those are different rooms because
/// they do different work.
/// </summary>
public enum PlotKind { Backyard, Counter, Seating }

public static class PlotText
{
    public static string Of(PlotKind kind) => kind switch
    {
        PlotKind.Backyard => "后院",
        PlotKind.Counter => "柜台",
        PlotKind.Seating => "座位",
        _ => kind.ToString()
    };

    public static bool TryParse(string? text, out PlotKind kind) =>
        Enum.TryParse(text, ignoreCase: true, out kind);
}

public static class ApparatusRules
{
    /// <summary>Which level a named piece of apparatus sits on.</summary>
    public static Housing HousingOf(ApparatusKind kind) => kind switch
    {
        ApparatusKind.Tarot or ApparatusKind.Crystal => Housing.Carried,
        ApparatusKind.Alembic => Housing.Basic,
        ApparatusKind.Observatory => Housing.Extension,
        _ => Housing.Carried
    };

    public static string HousingText(Housing h) => h switch
    {
        Housing.Carried => "随身",
        Housing.Ground => "区域内场地",
        Housing.Basic => "基础建筑",
        Housing.Extension => "扩展建筑",
        _ => h.ToString()
    };
}

/// <summary>Something carried: cards, a crystal. Goes where its owner goes.</summary>
public sealed class Carried
{
    public required ApparatusKind Kind { get; init; }
    public string Id { get; init; } = KitIds.New("carry");
    public DateTimeOffset GotAt { get; init; } = DateTimeOffset.UtcNow;
    public string? From { get; init; }
}

/// <summary>
/// A plot of ground inside a building. A workshop's backyard is one. It is not a building and
/// cannot be entered as one; what it does is give an extension somewhere to stand.
/// </summary>
public sealed class Plot
{
    public required PlotKind Kind { get; init; }
    public string Id { get; init; } = KitIds.New("plot");
    public string Name { get; init; } = "";
    public DateTimeOffset GotAt { get; init; } = DateTimeOffset.UtcNow;

    public string Label => string.IsNullOrWhiteSpace(Name) ? PlotText.Of(Kind) : Name;
}

/// <summary>
/// A building. A basic one stands alone and carries its own plots; an extension stands on a
/// plot that some basic building provides. Keeping those two facts in two shapes is what stops
/// "an observatory on bare ground" from ever being representable.
/// </summary>
public sealed class Building
{
    public required ApparatusKind Kind { get; init; }
    public string Id { get; init; } = KitIds.New("bld");

    /// <summary>Extension only: the plot it was built in.</summary>
    public string? OnPlot { get; init; }

    /// <summary>Basic only: the ground that came with the building. A workshop brings a backyard.</summary>
    public List<Plot> Plots { get; init; } = new();

    public DateTimeOffset GotAt { get; init; } = DateTimeOffset.UtcNow;

    public Housing Housing => ApparatusRules.HousingOf(Kind);
}

internal static class KitIds
{
    public static string New(string prefix) => prefix + "-" + Guid.NewGuid().ToString("N")[..8];
}

/// <summary>
/// The sky over the whole world. It sits above the scenes on purpose: everyone under the same
/// roof is under the same weather, and that shared fact is what makes a forecast worth paying
/// for. A character in one scene who hears rain on the window learns what is happening in the
/// other one without being told.
/// </summary>
public sealed class Sky
{
    public string Weather { get; set; } = "晴";
    public string Season { get; set; } = "春";

    /// <summary>How dark it is, 0..100. The observatory is the only place that reads this.</summary>
    public int Night { get; set; } = 20;

    /// <summary>Who last named the weather. Null means nobody has read it yet.</summary>
    public string? ForecastBy { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Words the sky listens for. An astrologer naming one of these gets it.</summary>
    public static readonly string[] Weathers = { "晴", "阴", "雨", "雪", "雾", "风", "雷" };

    public string Describe() => $"{Season}日，{Weather}，夜色 {Night}";
}

/// <summary>
/// Something one character told another. It is written into the listener's prompt, for the same
/// reason a nickname is: advice a model never sees is advice that never happened.
/// </summary>
public sealed class Advice
{
    public required string FromId { get; init; }
    public required string Text { get; init; }
    public DateTimeOffset At { get; init; } = DateTimeOffset.UtcNow;
    public bool Heeded { get; set; }
}

/// <summary>What came of one reading.</summary>
public sealed class DivinationOutcome
{
    public bool Ok { get; init; }
    public string? Reason { get; init; }
    public string Reading { get; init; } = "";
    public int Proficiency { get; init; }
    public bool Unlocked { get; init; }

    /// <summary>What the querent paid, and therefore what the reader earned.</summary>
    public int Earned { get; init; }
    public string? Advice { get; init; }
    public string? Weather { get; init; }
}

/// <summary>
/// Whether a character can read, and how well.
///
/// Four pieces make an astrologer, and they arrive at three levels: cards and a crystal are
/// carried, the workshop is a building that brings its own backyard, and the observatory is an
/// extension standing in that backyard. Proficiency is what turns any of it into a living, and
/// it is kept apart from the kit on purpose -- the kit is a decision, proficiency is time.
/// </summary>
public static class Astrologer
{
    public const string Skill = "占星";

    /// <summary>Proficiency at which readings start being paid for.</summary>
    public const int Livelihood = 30;

    /// <summary>Nobody carries more than this many pieces of advice in mind at once.</summary>
    public const int AdviceCap = 5;

    public static int Proficiency(Character c)
    {
        ArgumentNullException.ThrowIfNull(c);
        return c.Skills.TryGetValue(Skill, out var v) ? v : 0;
    }

    /// <summary>Looks on the right shelf for the right level. One entry point, two shelves.</summary>
    public static bool Has(Character c, ApparatusKind kind)
    {
        ArgumentNullException.ThrowIfNull(c);
        return ApparatusRules.HousingOf(kind) switch
        {
            Housing.Carried => c.Carried.Any(k => k.Kind == kind),
            Housing.Basic or Housing.Extension => c.Buildings.Any(b => b.Kind == kind),
            _ => false
        };
    }

    public static IReadOnlyList<ApparatusKind> Missing(Character c) =>
        Enum.GetValues<ApparatusKind>().Where(k => !Has(c, k)).ToArray();

    /// <summary>Finds which building provides a plot, and the plot itself.</summary>
    public static (Building Host, Plot Plot)? FindPlot(Character c, string plotId)
    {
        foreach (var b in c.Buildings)
        {
            var plot = b.Plots.FirstOrDefault(p => p.Id == plotId);
            if (plot is not null) return (b, plot);
        }
        return null;
    }

    /// <summary>
    /// Whether a building is actually standing. A basic building stands by itself, because its
    /// ground is part of it. An extension needs its plot to still exist inside a building that
    /// is still a basic building -- checked at read time rather than assumed, because a save
    /// file is just text and can arrive inconsistent.
    /// </summary>
    public static bool Standing(Character c, Building b)
    {
        ArgumentNullException.ThrowIfNull(c);
        ArgumentNullException.ThrowIfNull(b);
        if (b.Housing != Housing.Extension) return true;

        return b.OnPlot is not null
               && FindPlot(c, b.OnPlot) is { } home
               && home.Host.Housing == Housing.Basic;
    }

    /// <summary>Is the thing a character holds of that kind standing where it should be?</summary>
    public static bool Grounded(Character c, ApparatusKind kind)
    {
        if (!Has(c, kind)) return false;
        if (ApparatusRules.HousingOf(kind) == Housing.Carried) return true;
        return Standing(c, c.Buildings.First(b => b.Kind == kind));
    }

    /// <summary>
    /// All four, and every extension actually standing on a plot. That last clause is the whole
    /// point of the split: three of them can be acquired, but the observatory only exists
    /// because the workshop's backyard is already there.
    /// </summary>
    public static bool IsAstrologer(Character c) =>
        Missing(c).Count == 0 && c.Buildings.All(b => Standing(c, b));

    /// <summary>What one reading costs. Rises with proficiency, never past what a purse holds.</summary>
    public static int Price(Character c) => 1 + Math.Clamp(Proficiency(c) / 15, 0, 4);

    public static void Remember(Character listener, Advice advice)
    {
        listener.Advice.Add(advice);
        while (listener.Advice.Count > AdviceCap) listener.Advice.RemoveAt(0);
    }

    // --- acquiring -----------------------------------------------------------------------

    public static (Carried? Kit, string? Reason) Carry(Character owner, ApparatusKind kind, string? from = null)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var name = ApparatusText.Of(kind);
        if (ApparatusRules.HousingOf(kind) != Housing.Carried)
            return (null, $"{name}搬不动，它不是随身的东西");
        if (Has(owner, kind)) return (null, "已经有" + name);

        var kit = new Carried { Kind = kind, From = from };
        owner.Carried.Add(kit);
        return (kit, null);
    }

    /// <summary>
    /// Builds one. A basic building comes with its own ground -- a workshop is built with a
    /// backyard, and that backyard is where an extension can later go. An extension must name
    /// the plot it stands in, and that plot must still belong to a basic building.
    /// </summary>
    public static (Building? Building, string? Reason) Build(Character owner, ApparatusKind kind,
                                                             string? onPlot = null)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var name = ApparatusText.Of(kind);
        var housing = ApparatusRules.HousingOf(kind);
        if (housing == Housing.Carried) return (null, $"{name}是随身的东西，不用建");
        if (Has(owner, kind)) return (null, "已经有" + name);

        if (housing == Housing.Basic)
        {
            var basic = new Building { Kind = kind };
            basic.Plots.Add(new Plot { Kind = PlotKind.Backyard });
            owner.Buildings.Add(basic);
            return (basic, null);
        }

        if (string.IsNullOrWhiteSpace(onPlot))
            return (null, $"{name}是扩展建筑，得说清它建在哪块场地上（比如炼金工房的后院）");

        if (FindPlot(owner, onPlot) is not { } home) return (null, "没有这块场地：" + onPlot);
        if (home.Host.Housing != Housing.Basic)
            return (null, $"场地只属于基础建筑，{ApparatusText.Of(home.Host.Kind)}不是");

        var extension = new Building { Kind = kind, OnPlot = home.Plot.Id };
        owner.Buildings.Add(extension);
        return (extension, null);
    }

    /// <summary>Adds another plot to a basic building: a cellar, a roof, whatever comes next.</summary>
    public static (Plot? Plot, string? Reason) AddPlot(Character owner, string buildingId,
                                                       PlotKind kind, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var host = owner.Buildings.FirstOrDefault(b => b.Id == buildingId);
        if (host is null) return (null, "没有这座建筑：" + buildingId);
        if (host.Housing != Housing.Basic) return (null, "场地只能加在基础建筑上");

        var plot = new Plot { Kind = kind, Name = name ?? "" };
        host.Plots.Add(plot);
        return (plot, null);
    }
}

/// <summary>
/// One reading. Every tool reads a different thing, and the astrologer unlock is what turns
/// each of them from a curiosity into a service someone else will pay for.
///
/// The observatory does not forecast the weather; it names it. That is the only reading of the
/// sky this world allows, and it is the right one: the theatre has no dice, so nothing may roll
/// for tomorrow. If an astrologer says rain, it rains, and proficiency decides how far the
/// naming carries and how well they understood what they were naming.
/// </summary>
public static class Divination
{
    public static DivinationOutcome Perform(World world, RoundResult result, Character reader,
                                            ApparatusKind tool, Character? querent, string? question,
                                            string? offer = null)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(reader);

        var toolName = ApparatusText.Of(tool);
        if (!Astrologer.Has(reader, tool))
            return new DivinationOutcome { Ok = false, Reason = $"身上没有{toolName}" };
        if (!Astrologer.Grounded(reader, tool))
            return new DivinationOutcome { Ok = false, Reason = $"{toolName}没有立在该立的地方" };

        var before = Astrologer.Proficiency(reader);
        reader.Skills[Astrologer.Skill] = before + 1;
        var level = before + 1;
        var unlocked = Astrologer.IsAstrologer(reader);

        var earned = 0;
        string reading;
        string? weather = null;
        string? advice = null;

        switch (tool)
        {
            case ApparatusKind.Tarot:
                reading = ReadCards(reader, querent, level, result);
                break;

            case ApparatusKind.Crystal:
                reading = LookElsewhere(world, reader, querent, result);
                break;

            case ApparatusKind.Alembic:
                reading = Distil(reader, querent, result);
                break;

            default:
                weather = NameWeather(world, reader, question, level);
                reading = $"{reader.Id} 在后院里看了半宿，说天要{weather}";
                break;
        }

        // Being paid only starts once the practice is actually a practice. Before that the
        // reading is a favour, and a favour is worth remembering rather than billing.
        if (unlocked && level >= Astrologer.Livelihood && querent is not null && querent.Id != reader.Id)
        {
            var price = Math.Min(Astrologer.Price(reader), querent.Attributes.Reserve);
            if (price > 0)
            {
                querent.Attributes.Reserve -= price;
                reader.Attributes.Reserve = Attributes.Clamp(reader.Attributes.Reserve + price);
                reader.Attributes.Renown = Attributes.Clamp(reader.Attributes.Renown + 1);
                earned = price;
                result.Notes.Add($"{querent.Id} 付了 {price} 点余力给 {reader.Id}");
            }
        }

        if (unlocked && !string.IsNullOrWhiteSpace(offer) && querent is not null && querent.Id != reader.Id)
        {
            advice = Shorten(offer!, 60);
            Astrologer.Remember(querent, new Advice { FromId = reader.Id, Text = advice });
            result.Notes.Add($"{reader.Id} 给 {querent.Id} 留了一句话：「{advice}」");
        }

        reader.Attributes.Insight = Attributes.Clamp(reader.Attributes.Insight + 1);
        reader.Traits.Shift("敏锐", 1);
        if (earned > 0) reader.Traits.Shift("精明", 1);

        result.Revealed.Add(reading);
        result.Notes.Add($"{reader.Id} 用{toolName}占了一次（熟练度 {before} → {level}）");

        return new DivinationOutcome
        {
            Ok = true,
            Reading = reading,
            Proficiency = level,
            Unlocked = unlocked,
            Earned = earned,
            Advice = advice,
            Weather = weather
        };
    }

    /// <summary>
    /// The cards read a person. What they show is drawn from what that person is actually
    /// holding back, so a reading is never a compliment -- it is a specific secret, surfaced.
    /// Deterministic by proficiency: the same reader asks the same person and gets the same
    /// card until one of them has moved.
    /// </summary>
    private static string ReadCards(Character reader, Character? querent, int level, RoundResult result)
    {
        if (querent is null) return $"{reader.Id} 洗了牌，牌面朝下，没有问谁";

        var pool = new List<string>();
        pool.AddRange(querent.Sheet.Secrets);
        pool.AddRange(querent.Sheet.Fears);
        if (pool.Count == 0)
            return $"{reader.Id} 摊开牌，{querent.Id} 的牌面是空的：这个人身上没有藏起来的东西";

        var card = pool[level % pool.Count];
        result.Notes.Add($"牌面：{Shorten(card, 50)}");
        return $"{reader.Id} 摊开牌，指着 {querent.Id} 说：{Shorten(card, 50)}";
    }

    /// <summary>
    /// The crystal shows somewhere the reader is not standing. In a world with several scenes
    /// at once this is the only way to learn that another room exists, which makes it the tool
    /// that turns a resident into someone who knows things.
    /// </summary>
    private static string LookElsewhere(World world, Character reader, Character? querent, RoundResult result)
    {
        var other = world.Scenes
            .Where(s => s.Open && s.Cast.Active.Any(id => id != reader.Id))
            .Where(s => !s.Cast.Active.Contains(reader.Id))
            .OrderBy(s => s.Number)
            .FirstOrDefault();

        reader.Attributes.Insight = Attributes.Clamp(reader.Attributes.Insight + 1);

        if (other is null)
            return $"{reader.Id} 盯着水晶，只看见自己这一间屋子，别处没有人";

        var who = string.Join("、", other.Cast.Active.Take(4));
        var may = Astrologer.Proficiency(reader) >= Astrologer.Livelihood;
        var line = may
            ? $"{reader.Id} 在水晶里看见 {other.Id}：{who} 在那边"
            : $"{reader.Id} 在水晶里看见别处有影子动了一下，认不出是谁";
        result.Notes.Add(line);
        return line;
    }

    /// <summary>
    /// The alembic turns time into something you can carry. It is the only tool that gives back
    /// reserve rather than information, which is why it is the one people fight over.
    /// </summary>
    private static string Distil(Character reader, Character? querent, RoundResult result)
    {
        if (querent is null || querent.Id == reader.Id)
        {
            reader.Attributes.Reserve = Attributes.Clamp(reader.Attributes.Reserve + 3);
            return $"{reader.Id} 炼了一炉东西出来，自己收着（余力 +3）";
        }

        querent.Attributes.Strain = Attributes.Clamp(querent.Attributes.Strain - 6);
        reader.Attributes.Reserve = Attributes.Clamp(reader.Attributes.Reserve + 2);
        result.Notes.Add($"{querent.Id} 的压力降了 6");
        return $"{reader.Id} 给 {querent.Id} 调了一副药，{querent.Id} 喝下去，肩膀松了些";
    }

    /// <summary>
    /// Names the weather. A word in the question is taken first -- say rain and it rains -- and
    /// with nothing said the sky picks by proficiency, so an experienced astrologer tends to
    /// name the weather that was already coming.
    /// </summary>
    private static string NameWeather(World world, Character reader, string? question, int level)
    {
        var asked = Sky.Weathers.FirstOrDefault(w => question is not null && question.Contains(w));
        var weather = asked ?? Sky.Weathers[level % Sky.Weathers.Length];

        world.Sky.Weather = weather;
        world.Sky.ForecastBy = reader.Id;
        world.Sky.At = DateTimeOffset.UtcNow;

        // Night deepens with proficiency: someone who reads the sky for a living stays out later.
        world.Sky.Night = Math.Clamp(world.Sky.Night + 1 + level / 10, 0, 100);

        // And the sky carries to everyone: nobody has to be told twice.
        foreach (var scene in world.Scenes.Where(s => s.Open))
            scene.Record(reader.Id, "narration", $"天色变了：{world.Sky.Describe()}");

        return weather;
    }

    private static string Shorten(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";
}
