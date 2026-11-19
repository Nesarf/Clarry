namespace Clarry.Core;

/// <summary>
/// A game the characters may play inside the story. The theatre's own loop is always the
/// simultaneous dramatic round; a mini-game is an interlude, never the engine.
///
/// Two rules keep these from swallowing the design:
///   - the world never rolls dice; a tabletop game rolls on behalf of characters only
///   - a strategy game must be deterministic, so the same inputs reproduce the same match
/// </summary>
public interface IMiniGame
{
    string Id { get; }
    string Title { get; }

    /// <summary>Whether this game can start given the world's current state.</summary>
    bool CanStart(World world, IReadOnlyList<Character> players);

    /// <summary>Play it out and hand back what the story should absorb.</summary>
    MiniGameResult Play(World world, IReadOnlyList<Character> players);
}

/// <summary>What a finished mini-game leaves behind.</summary>
public sealed class MiniGameResult
{
    public required string GameId { get; init; }

    /// <summary>Who came out ahead, for a game that has an ahead. Left null by co-op games.</summary>
    public string? WinnerId { get; set; }

    /// <summary>
    /// Whether the group pulled it off. Left null by games that only produce a winner.
    /// A co-op game sets this and leaves <see cref="WinnerId"/> empty on purpose: a group
    /// that holds together until morning has nobody who won it.
    /// </summary>
    public bool? TeamSucceeded { get; set; }

    public List<string> Log { get; } = new();

    /// <summary>Changes the main story should apply when the interlude ends.</summary>
    public Dictionary<string, AttributeDelta> Effects { get; } = new(StringComparer.Ordinal);

    /// <summary>True when randomness was used. Only tabletop games and card shuffles set this.</summary>
    public bool UsedDice { get; init; }
}

/// <summary>
/// A deterministic strategy match in the shape of a 4X board game: tiles, cities, a tech
/// ladder, and simultaneous rounds. Deliberately small -- the point is that agents have
/// something structured to contest, not that the theatre becomes a strategy game.
/// </summary>
public sealed class CivLikeGame : IMiniGame
{
    public string Id => "civ-like";
    public string Title => "A small 4X board";

    public int MaxPlayers { get; init; } = 4;
    public int Rounds { get; init; } = 10;
    public int GridSize { get; init; } = 8;

    public bool CanStart(World world, IReadOnlyList<Character> players)
        => players.Count >= 2 && players.Count <= MaxPlayers;

    public MiniGameResult Play(World world, IReadOnlyList<Character> players)
    {
        if (!CanStart(world, players))
            throw new InvalidOperationException($"civ-like needs 2..{MaxPlayers} players");

        var result = new MiniGameResult { GameId = Id, UsedDice = false };
        var score = players.ToDictionary(p => p.Id, _ => 0, StringComparer.Ordinal);
        var cities = players.ToDictionary(p => p.Id, _ => 1, StringComparer.Ordinal);

        // Deterministic turn order: the stage order the caller passed in, nothing else.
        for (var round = 1; round <= Rounds; round++)
        {
            foreach (var p in players)
            {
                // A fixed expansion law: cities grow by renown, score compounds by cities.
                var growth = 1 + (p.Attributes.Renown / 50);
                cities[p.Id] += growth;
                score[p.Id] += cities[p.Id] * 2;
            }
            var leader = players.OrderByDescending(p => score[p.Id]).First();
            result.Log.Add($"round {round}: leader {leader.Id} ({score[leader.Id]})");
        }

        var winner = players.OrderByDescending(p => score[p.Id]).First();
        result.WinnerId = winner.Id;

        foreach (var p in players)
        {
            var delta = p.Id == winner.Id
                ? new AttributeDelta(0, 0, 4, -2, 2)   // winning steadies you
                : new AttributeDelta(0, 0, -1, 1, -1);  // losing costs a little
            result.Effects[p.Id] = delta;
        }

        return result;
    }
}

/// <summary>
/// A small deterministic RNG. Written out rather than using System.Random because the whole
/// point of a seeded shuffle is that the same seed yields the same order on any runtime and
/// any version -- a promise System.Random does not make.
/// </summary>
internal sealed class DeterministicRng
{
    private uint _state;

    public DeterministicRng(uint seed) => _state = seed == 0 ? 0x9E3779B9u : seed;

    public uint Next()
    {
        _state ^= _state << 13;
        _state ^= _state >> 17;
        _state ^= _state << 5;
        return _state;
    }

    public int Next(int maxExclusive) => maxExclusive <= 0 ? 0 : (int)(Next() % (uint)maxExclusive);
}

/// <summary>
/// A two-player card duel in the shape of Weiss Schwarz: cards have a level they demand and a
/// soul value they deal, and soul damage pushes you up a level -- reaching level four is how
/// you lose. Getting knocked out of a story by everything you failed to absorb fits this
/// theatre better than a hit-point bar does.
///
/// Deliberately reduced: no stock, no assists, no counter triggers, no deck building. What is
/// kept is the one structure worth keeping -- damage that promotes rather than subtracts.
///
/// The shuffle is seeded from the world and the two seats, so the same pairing on the same
/// round always deals the same cards. Luck is inside the interlude and never leaks out of it.
/// </summary>
public sealed class CardDuelGame : IMiniGame
{
    public string Id => "card-duel";
    public string Title => "黑白对局";

    public int HandSize { get; init; } = 5;
    public int MaxTurns { get; init; } = 24;

    private readonly record struct Card(int Level, int Power, int Soul);

    private sealed class Duelist
    {
        public required string Id { get; init; }
        public List<Card> Deck { get; } = new();
        public List<Card> Hand { get; } = new();
        public int Level;
        public int Damage;
        public bool Lost;
    }

    /// <summary>How much accumulated soul damage it takes to climb out of each level.</summary>
    private static readonly int[] LevelUpAt = { 6, 7, 8, 9 };

    public bool CanStart(World world, IReadOnlyList<Character> players)
        => players.Count == 2;

    public MiniGameResult Play(World world, IReadOnlyList<Character> players)
    {
        if (!CanStart(world, players))
            throw new InvalidOperationException("card-duel 需要正好两个人");

        var seed = SeedFor(world, players);
        var result = new MiniGameResult { GameId = Id, UsedDice = true };
        result.Log.Add($"洗牌种子 {seed}：同一个世界、同样两个人，摸到的永远是这副牌");

        var duelists = players
            .Select(p => BuildDuelist(p.Id, seed))
            .ToList();

        foreach (var d in duelists) Draw(d, HandSize, result);

        for (var turn = 1; turn <= MaxTurns; turn++)
        {
            if (duelists.Count(d => !d.Lost) <= 1) break;

            var attacker = duelists[(turn - 1) % 2];
            var defender = duelists[turn % 2];
            if (attacker.Lost || defender.Lost) continue;

            var attack = BestPlayable(attacker);
            if (attack is null)
            {
                Draw(attacker, 1, result);
                result.Log.Add($"第 {turn} 回合：{attacker.Id} 手上没有能出的牌，抽一张过去");
                continue;
            }

            var guard = BestPlayable(defender);
            attacker.Hand.Remove(attack.Value);
            if (guard is not null) defender.Hand.Remove(guard.Value);

            var guardPower = guard?.Power ?? 0;
            if (attack.Value.Power > guardPower)
            {
                var soul = attack.Value.Soul;
                defender.Damage += soul;
                result.Log.Add($"第 {turn} 回合：{attacker.Id} 以 {attack.Value.Power} 打穿了 {defender.Id}（拦 {guardPower}），"
                             + $"灵魂 {soul} 点");
                Climb(defender, result);
            }
            else
            {
                attacker.Damage += 1;
                result.Log.Add($"第 {turn} 回合：{attacker.Id} 的 {attack.Value.Power} 被 {defender.Id} 拦住（{guardPower}），反噬 1 点");
                Climb(attacker, result);
            }

            Draw(attacker, 1, result);
            Draw(defender, 1, result);
        }

        var loser = duelists.FirstOrDefault(d => d.Lost);
        var winner = loser is null
            ? duelists.OrderBy(d => d.Level).ThenBy(d => d.Damage).First()
            : duelists.First(d => d.Id != loser.Id);

        result.WinnerId = winner.Id;
        result.Log.Add($"收局：{winner.Id} 胜（{winner.Id} {winner.Level} 级，{duelists.First(d => d.Id != winner.Id).Id} "
                     + $"{duelists.First(d => d.Id != winner.Id).Level} 级）");

        foreach (var p in players)
        {
            result.Effects[p.Id] = p.Id == winner.Id
                ? new AttributeDelta(0, 1, 3, -1, -1)   // winning sharpens, and calms
                : new AttributeDelta(0, 0, -1, 2, -2);  // losing rankles
        }

        return result;
    }

    private static Duelist BuildDuelist(string id, uint seed)
    {
        var rng = new DeterministicRng(seed ^ GameSeed.Fnv1a(id));
        var d = new Duelist { Id = id };

        for (var i = 0; i < 8; i++) d.Deck.Add(new Card(0, 500 + rng.Next(4) * 500, 1));
        for (var i = 0; i < 8; i++) d.Deck.Add(new Card(1, 1000 + rng.Next(5) * 500, 1));
        for (var i = 0; i < 4; i++) d.Deck.Add(new Card(2, 2000 + rng.Next(3) * 500, 2));

        for (var i = d.Deck.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (d.Deck[i], d.Deck[j]) = (d.Deck[j], d.Deck[i]);
        }

        return d;
    }

    private static Card? BestPlayable(Duelist d)
    {
        Card? best = null;
        foreach (var card in d.Hand)
        {
            if (card.Level > d.Level) continue;
            if (best is null || card.Power > best.Value.Power) best = card;
        }
        return best;
    }

    private static void Draw(Duelist d, int count, MiniGameResult result)
    {
        for (var i = 0; i < count && d.Deck.Count > 0; i++)
        {
            var top = d.Deck[^1];
            d.Deck.RemoveAt(d.Deck.Count - 1);
            d.Hand.Add(top);
        }
    }

    /// <summary>Soul damage promotes rather than subtracts: level four ends the duel.</summary>
    private static void Climb(Duelist d, MiniGameResult result)
    {
        while (d.Level < 4)
        {
            var need = LevelUpAt[Math.Min(d.Level, LevelUpAt.Length - 1)];
            if (d.Damage < need) break;

            d.Damage -= need;
            d.Level++;
            result.Log.Add($"{d.Id} 承受到 {d.Level} 级");

            if (d.Level >= 4)
            {
                d.Lost = true;
                result.Log.Add($"{d.Id} 到了 4 级，出局");
            }
        }
    }

    private static uint SeedFor(World world, IReadOnlyList<Character> players) =>
        GameSeed.For(world, players, "card-duel");
}

/// <summary>
/// One place that decides what a game's seed is, so two games played by the same people in the
/// same world do not accidentally share a shuffle. The salt is the game id and nothing else:
/// the same players replaying the same game get the same board, which is the promise a seeded
/// tabletop makes.
/// </summary>
internal static class GameSeed
{
    public static uint For(World world, IReadOnlyList<Character> players, string salt)
    {
        var text = $"{salt}|{world.Id}|{world.Rounds.Count}|{string.Join(",", players.Select(p => p.Id))}";
        return Fnv1a(text);
    }

    internal static uint Fnv1a(string text)
    {
        var hash = 2166136261u;
        foreach (var ch in text)
        {
            hash ^= ch;
            hash *= 16777619u;
        }
        return hash == 0 ? 1u : hash;
    }
}

/// <summary>
/// A co-operative interlude: keep a watch until morning. Each watch demands more combined effort
/// than the last, and what each person contributes is drawn from who they are -- poise keeps
/// you upright, reserve keeps you awake. Nobody wins this. Either the group holds, or it
/// does not, and the difference is what it does to everyone at once.
///
/// It exists because every other game in this theatre produces a winner, and a group that
/// only ever competes has no way to become close except by talking about it.
/// </summary>
public sealed class VigilGame : IMiniGame
{
    public string Id => "vigil";
    public string Title => "守夜";

    public int Rounds { get; init; } = 5;
    public int MaxPlayers { get; init; } = 4;

    /// <summary>How much combined effort each watch demands. Rises faster than people add up.</summary>
    private static readonly int[] Demands = { 3, 4, 5, 6, 7 };

    /// <summary>One slip is survivable; two means the fire went out.</summary>
    public int SlipsAllowed { get; init; } = 1;

    public bool CanStart(World world, IReadOnlyList<Character> players)
        => players.Count >= 2 && players.Count <= MaxPlayers;

    public MiniGameResult Play(World world, IReadOnlyList<Character> players)
    {
        if (!CanStart(world, players))
            throw new InvalidOperationException($"守夜需要 2 到 {MaxPlayers} 个人");

        var result = new MiniGameResult { GameId = Id, UsedDice = false };
        var slips = 0;

        for (var round = 1; round <= Rounds; round++)
        {
            var need = Demands[Math.Min(round - 1, Demands.Length - 1)];
            var total = 0;
            var parts = new List<string>();

            foreach (var p in players)
            {
                // Poise keeps you upright, reserve keeps you awake. Integer maths on purpose:
                // a contribution is a whole unit of effort, not a fraction.
                var share = p.Attributes.Poise / 20 + p.Attributes.Reserve / 40;
                if (share < 0) share = 0;
                total += share;
                parts.Add($"{p.Id} 出力 {share}");
            }

            if (total >= need)
                result.Log.Add($"第 {round} 更：{string.Join("、", parts)}，合计 {total} 撑住 {need}");
            else
            {
                slips++;
                result.Log.Add($"第 {round} 更：{string.Join("、", parts)}，合计 {total} 不足 {need}，有人闭了眼");
            }
        }

        var held = slips <= SlipsAllowed;
        result.TeamSucceeded = held;
        result.Log.Add(held
            ? $"天亮了。{players.Count} 个人一起把它守了过去（失守 {slips} 次）"
            : $"没撑到天亮，失守 {slips} 次");

        foreach (var p in players)
        {
            result.Effects[p.Id] = held
                ? new AttributeDelta(1, 0, 3, -2, -1)   // shared relief: steadier, lighter, tired
                : new AttributeDelta(0, 0, -1, 4, -2);  // shared failure: heavier, spent
        }

        return result;
    }
}