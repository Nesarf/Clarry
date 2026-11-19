namespace Clarry.Core;

/// <summary>
/// 大富翁 -- the board game where the board is the story.
///
/// Twenty-four tiles around a ring: property to buy, a tax collector to dread, a few chance
/// squares, and the start you keep coming back to. Two to four players, laps until one of them
/// is out or the turn budget runs dry, and a winner decided by what they are worth rather than
/// by what they are holding.
///
/// The rolling is real random, but it is seeded, for the same reason a card shuffle is: two
/// people playing this in the same world on the same round must see the same board, or a replay
/// of the story would not replay. That is the only dice this theatre permits and it stays
/// inside the interlude.
///
/// Buying is decided by who the buyer is rather than by a policy flag. A character with renown
/// will overreach for a good street; a cautious one waits until the price is small against the
/// purse. Nobody has to give orders for the game to be worth watching -- it is a way of finding
/// out what someone does with money, and the answer should come from their card.
/// </summary>
public sealed class BoardGame : IMiniGame
{
    public string Id => "board";
    public string Title => "大富翁";

    public int MaxPlayers { get; init; } = 4;
    public int MaxTurns { get; init; } = 60;

    /// <summary>
    /// Starting money. Measured against the board rather than guessed: the cheapest street is
    /// sixty and the dearest is two-eighty, and a purse of a hundred and fifty could buy two
    /// deeds and then nothing until the next lap. That produced matches where three of four
    /// players went broke inside ten laps, which is not a game, it is a queue at a tax office.
    /// </summary>
    public int StartingPurse { get; init; } = 250;

    public bool CanStart(World world, IReadOnlyList<Character> players)
        => players.Count >= 2 && players.Count <= MaxPlayers;

    private enum TileKind { Start, Property, Tax, Chance, Rest, Empty }

    private readonly record struct Tile(TileKind Kind, string Name, int Price);

    /// <summary>
    /// The ring. Prices climb as you go round, so the far side of the board is where the money
    /// is and the near side is where everyone starts. Chance and the tax collector are placed
    /// so that no stretch of three tiles is free of either.
    /// </summary>
    private static readonly Tile[] Tiles =
    {
        new(TileKind.Start,    "起点",     0),
        new(TileKind.Property, "旧书摊",   60),
        new(TileKind.Property, "神学校",   90),
        new(TileKind.Chance,   "命运",     0),
        new(TileKind.Empty,    "中庭",     0),
        new(TileKind.Property, "面包房",  100),
        new(TileKind.Tax,      "税务官",   30),
        new(TileKind.Property, "钟楼",    120),
        new(TileKind.Chance,   "命运",     0),
        new(TileKind.Property, "旧码头",  140),
        new(TileKind.Rest,     "歇脚处",   0),
        new(TileKind.Property, "药草园",  150),
        new(TileKind.Property, "采石场",  170),
        new(TileKind.Chance,   "命运",     0),
        new(TileKind.Property, "炼金街",  180),
        new(TileKind.Tax,      "税务官",   50),
        new(TileKind.Property, "天文台",  200),
        new(TileKind.Chance,   "命运",     0),
        new(TileKind.Property, "王城大道", 220),
        new(TileKind.Empty,    "中庭",     0),
        new(TileKind.Property, "旧剧场",  240),
        new(TileKind.Chance,   "命运",     0),
        new(TileKind.Property, "龙脊庭院", 280),
        new(TileKind.Tax,      "税务官",   80)
    };

    /// <summary>One person walking the ring. Purse is money; everything else is position.</summary>
    private sealed class Traveller
    {
        public required string Id { get; init; }
        public int Purse { get; set; }
        public int At { get; set; }
        public List<int> Owned { get; } = new();
        public HashSet<int> Mortgaged { get; } = new();
        public bool Out { get; set; }

        /// <summary>What they are worth: cash plus half of every un-mortgaged deed.</summary>
        public int Worth() => Purse + Owned.Where(i => !Mortgaged.Contains(i)).Sum(i => Tiles[i].Price / 2);
    }

    public MiniGameResult Play(World world, IReadOnlyList<Character> players)
    {
        if (!CanStart(world, players))
            throw new InvalidOperationException($"大富翁需要 2 到 {MaxPlayers} 个人");

        var seed = GameSeed.For(world, players, Id);
        var rng = new DeterministicRng(seed);
        var result = new MiniGameResult { GameId = Id, UsedDice = true };
        result.Log.Add($"棋盘种子 {seed}：同一个世界、同样几个人，走出来的永远是这一局");

        var byId = players.ToDictionary(p => p.Id, StringComparer.Ordinal);
        var crew = players.Select(p => new Traveller { Id = p.Id, Purse = StartingPurse }).ToList();

        // Whoever the caller put first walks first. Turn order is never decided by the seed:
        // a game where the seat order changes the outcome must not also change it per roll.
        var turn = 0;
        while (turn < MaxTurns && crew.Count(t => !t.Out) > 1)
        {
            turn++;
            foreach (var mover in crew.Where(t => !t.Out).ToArray())
            {
                if (mover.Out) continue;
                if (crew.Count(t => !t.Out) <= 1) break;

                var steps = rng.Next(6) + rng.Next(6) + 2;
                mover.At = (mover.At + steps) % Tiles.Length;
                var tile = Tiles[mover.At];
                var self = byId[mover.Id];

                switch (tile.Kind)
                {
                    case TileKind.Property:
                        StepOnProperty(mover, self, tile, crew, byId, turn, result);
                        break;

                    case TileKind.Tax:
                        mover.Purse -= tile.Price;
                        result.Log.Add($"第 {turn} 回合：{mover.Id} 走到{tile.Name}，交出去 {tile.Price}");
                        break;

                    case TileKind.Chance:
                        TakeChance(mover, rng, turn, result);
                        break;

                    case TileKind.Start:
                        // Passing the start is the one thing everyone gets for free, and it has
                        // to be worth more than one tax square or a lap stops being a reward.
                        mover.Purse += 50;
                        result.Log.Add($"第 {turn} 回合：{mover.Id} 回到{tile.Name}，领了 50");
                        break;

                    // Rest and empty tiles do nothing on purpose: a board where every tile
                    // costs something has no shape, only a slope.
                }

                if (mover.Purse < 0) Settle(mover, turn, result);
            }
        }

        // Ranked by worth rather than by cash: a purse that has been turned into deeds is not
        // a smaller purse, and a game that scored it as one would teach the wrong lesson.
        var ranked = (crew.Count(t => !t.Out) > 0 ? crew.Where(t => !t.Out) : crew)
            .OrderByDescending(t => t.Worth())
            .ThenBy(t => t.Id, StringComparer.Ordinal)
            .ToList();

        var winner = ranked[0];
        result.WinnerId = winner.Id;

        result.Log.Add($"收局：{winner.Id} 以 {winner.Worth()} 的身家赢了"
                     + $"（{winner.Owned.Count} 张地契，其中抵押 {winner.Mortgaged.Count}）");

        foreach (var p in players)
        {
            var walker = crew.First(t => t.Id == p.Id);
            result.Effects[p.Id] = p.Id == winner.Id
                ? new AttributeDelta(0, 0, 5, -2, 3)      // winning steadies you
                : walker.Out
                    ? new AttributeDelta(0, 0, -4, 5, -3)  // going broke in public
                    : new AttributeDelta(0, 0, -2, 2, -1); // merely losing costs a little
        }

        return result;
    }

    private void StepOnProperty(Traveller mover, Character self, Tile tile, List<Traveller> crew,
                                Dictionary<string, Character> byId, int turn, MiniGameResult result)
    {
        var at = mover.At;
        var owner = crew.FirstOrDefault(t => t.Owned.Contains(at));

        if (owner is null)
        {
            // What a person does with money comes from their card, not from a policy setting.
            // Two ways to buy: a price small enough that the purse still holds more than it
            // spent (anyone does this), or renown enough to spend down to nothing on purpose
            // (only someone who wants to be seen owning it).
            //
            // Measured against the purse rather than against a fixed multiple, because the
            // first version asked for three times the price and the purse held two and a half,
            // so a whole match went by in which nobody could buy anything.
            var price = tile.Price;
            var careful = price * 2 <= mover.Purse;
            var overreach = self.Attributes.Renown >= 50 && price <= mover.Purse;

            if (careful || overreach)
            {
                mover.Purse -= price;
                mover.Owned.Add(at);
                result.Log.Add($"第 {turn} 回合：{mover.Id} 买下{tile.Name}，付出 {price}");
            }
            else
            {
                result.Log.Add($"第 {turn} 回合：{mover.Id} 在{tile.Name}前站了一会儿，没买（{price}，手里 {mover.Purse}）");
            }
            return;
        }

        if (owner.Id == mover.Id)
        {
            result.Log.Add($"第 {turn} 回合：{mover.Id} 回到自己的{tile.Name}");
            return;
        }

        if (owner.Mortgaged.Contains(at))
        {
            result.Log.Add($"第 {turn} 回合：{mover.Id} 走到{owner.Id} 抵押出去的{tile.Name}，不用付");
            return;
        }

        var landlord = byId[owner.Id];
        var rent = Math.Max(5, tile.Price / 4 + landlord.Attributes.Renown / 20);
        rent = Math.Min(rent, mover.Purse);
        mover.Purse -= rent;
        owner.Purse += rent;
        result.Log.Add($"第 {turn} 回合：{mover.Id} 走到{owner.Id} 的{tile.Name}，付租 {rent}");
    }

    /// <summary>
    /// Chance never takes the form of a decision. It is the one part of this board nobody can
    /// play well, which is exactly what makes it read as luck rather than as policy.
    /// </summary>
    private static void TakeChance(Traveller mover, DeterministicRng rng, int turn, MiniGameResult result)
    {
        switch (rng.Next(4))
        {
            case 0:
                mover.Purse += 45;
                result.Log.Add($"第 {turn} 回合：{mover.Id} 在路边捡到 45");
                break;
            case 1:
                mover.Purse -= 35;
                result.Log.Add($"第 {turn} 回合：{mover.Id} 赔了 35 给不该赔的人");
                break;
            case 2:
                mover.Purse += 20;
                result.Log.Add($"第 {turn} 回合：{mover.Id} 收到一笔旧账，20");
                break;
            default:
                mover.Purse -= 15;
                result.Log.Add($"第 {turn} 回合：{mover.Id} 掉了 15，回头已经找不到");
                break;
        }
    }

    /// <summary>
    /// Out of cash. Deeds go to the lender one at a time, dearest first, until the purse is
    /// whole again; whatever cannot be covered that way is a bankruptcy, and a bankruptcy is
    /// the loudest thing that can happen to a character in a game about money.
    /// </summary>
    private static void Settle(Traveller mover, int turn, MiniGameResult result)
    {
        foreach (var idx in mover.Owned.OrderByDescending(i => Tiles[i].Price))
        {
            if (mover.Purse >= 0) break;
            if (mover.Mortgaged.Contains(idx)) continue;

            mover.Mortgaged.Add(idx);
            mover.Purse += Tiles[idx].Price / 2;
            result.Log.Add($"第 {turn} 回合：{mover.Id} 把{Tiles[idx].Name}押了出去，换回 {Tiles[idx].Price / 2}");
        }

        if (mover.Purse >= 0) return;

        mover.Out = true;
        result.Log.Add($"第 {turn} 回合：{mover.Id} 还不上，出局");
    }
}
