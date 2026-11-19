namespace Clarry.Core;

using System.Text.Json.Serialization;

/// <summary>What kind of place this is. It flavours the name and the suggested menu, nothing else.</summary>
public enum VenueKind { Cafe, Bar, Shop }

public static class VenueText
{
    public static string Of(VenueKind kind) => kind switch
    {
        VenueKind.Cafe => "咖啡馆",
        VenueKind.Bar => "酒馆",
        VenueKind.Shop => "杂货铺",
        _ => kind.ToString()
    };

    public static bool TryParse(string? text, out VenueKind kind) =>
        Enum.TryParse(text, ignoreCase: true, out kind);
}

/// <summary>One ingredient a dish consumes. Kept as plain text plus a count, like the rules of an activity.</summary>
public sealed class Need
{
    public required string Item { get; init; }
    public int Count { get; init; } = 1;
}

/// <summary>
/// A dish: what it is called, what it costs, what it eats out of the larder, and how much of a
/// shift it takes to make. The price is the owner's decision; the effort is why a place can only
/// serve so many people before the evening ends.
/// </summary>
public sealed class MenuItem
{
    public required string Name { get; init; }
    public int Price { get; init; }
    public int Effort { get; init; } = 1;
    public List<Need> Needs { get; init; } = new();
}

/// <summary>What is in the larder. Count is what is left; below zero never happens.</summary>
public sealed class StockItem
{
    public required string Name { get; init; }
    public int Count { get; set; }
}

/// <summary>Someone working here. Staff are supporting characters: they never take a stage seat.</summary>
public sealed class Staff
{
    public required string CharacterId { get; init; }
    public string Role { get; init; } = "店员";
    public int Wage { get; init; }
    public bool OnDuty { get; set; } = true;
}

/// <summary>Which way the money went. Sales come in; supply and wages go out.</summary>
public enum LedgerKind { Sale, Supply, Wage }

/// <summary>One line of the books. Nothing here is derived -- the ledger is the record, not a total.</summary>
public sealed class LedgerLine
{
    public required LedgerKind Kind { get; init; }
    public int Amount { get; init; }

    /// <summary>Positive means the venue gained, negative means it paid out.</summary>
    public string? Note { get; init; }
    public string? WhoId { get; init; }
    public DateTimeOffset At { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// A place that serves people. It belongs to one character, it stands on its own ground the way
/// a basic building does, and it brings its own rooms: a counter, somewhere to sit, and whatever
/// else the owner adds.
///
/// It is deliberately not a <see cref="Building"/>. A building is something a character owns and
/// works in alone; a venue has a menu, a larder, staff and books, and those have nothing to do
/// with whether anyone can read the sky from the roof. Keeping them separate means a workshop
/// and a cafe can share a rule about ground without sharing a shape.
/// </summary>
public sealed class Venue
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string OwnerId { get; init; }
    public VenueKind Kind { get; init; } = VenueKind.Cafe;

    /// <summary>Shut until the owner opens it. A shop that was never opened cannot sell anything.</summary>
    public bool Open { get; set; }

    /// <summary>
    /// What the neighbourhood thinks of the place, 0..100. It decides how many people are willing
    /// to stand and wait, and it moves with every guest served or lost. That is the whole feedback
    /// loop: a good evening brings a longer line, and a line longer than the staff can serve is
    /// how a good name turns into a bad one.
    /// </summary>
    public int Reputation { get; set; } = 50;

    /// <summary>How many evenings this place has been through. History, not a limit.</summary>
    public int DaysOpen { get; set; }

    /// <summary>
    /// The evening in progress. Deliberately not saved: a queue is what is happening right now,
    /// the way a round is. The place, its books, its menu and its name survive a restart.
    /// </summary>
    [JsonIgnore]
    public Shift? Shift { get; set; }

    /// <summary>The rooms that came with the place: a counter, seating, and whatever is added.</summary>
    public List<Plot> Plots { get; init; } = new();

    public List<MenuItem> Menu { get; init; } = new();
    public List<StockItem> Stock { get; init; } = new();
    public List<Staff> Staff { get; init; } = new();
    public List<LedgerLine> Ledger { get; init; } = new();

    public DateTimeOffset OpenedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Takings minus what was spent getting them there.</summary>
    public int Balance => Ledger.Sum(l => l.Amount);

    public int StockOf(string item) =>
        Stock.FirstOrDefault(s => string.Equals(s.Name, item, StringComparison.Ordinal))?.Count ?? 0;

    public MenuItem? Dish(string name) =>
        Menu.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.Ordinal));

    public int OnDutyCount => Staff.Count(s => s.OnDuty);
}

/// <summary>What one guest got, so the story can absorb it without reading the books.</summary>
public sealed class Receipt
{
    public required string VenueId { get; init; }
    public required string Dish { get; init; }
    public int Price { get; init; }
    public required string GuestId { get; init; }
    public string? ServedById { get; init; }
    public AttributeDelta Effects { get; init; } = AttributeDelta.Zero;
}

/// <summary>
/// Running a place. Every function here either happens or says why it did not -- there is no
/// "partly served", because a half order is a data bug wearing a friendly face.
///
/// Money in this theatre is reserve: the design has said so from the start, and a second currency
/// would mean a second set of exchange rates nobody asked for. So a sale moves reserve from the
/// guest to the owner, and the books record it.
/// </summary>
public static class Trade
{
    /// <summary>How many rooms come with a new place, and which ones.</summary>
    private static readonly PlotKind[] ComesWith = { PlotKind.Counter, PlotKind.Seating };

    public static (Venue? Venue, string? Reason) Open(World world, Character owner, string name,
                                                      VenueKind kind = VenueKind.Cafe)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(owner);

        if (string.IsNullOrWhiteSpace(name)) return (null, "店总得有个名字");

        var taken = world.Venues.Any(v => string.Equals(v.Name, name.Trim(), StringComparison.Ordinal));
        if (taken) return (null, "已经有一家叫「" + name.Trim() + "」的店了");

        var venue = new Venue
        {
            Id = "venue-" + (world.Venues.Count + 1),
            Name = name.Trim(),
            OwnerId = owner.Id,
            Kind = kind
        };

        // The room it comes with.

        foreach (var plot in ComesWith)
            venue.Plots.Add(new Plot { Kind = plot });

        world.Venues.Add(venue);
        return (venue, null);
    }

    /// <summary>
    /// Adds a dish. The needs are the whole point: a dish that costs nothing to make would let a
    /// place sell forever without ever buying anything, which is not a business.
    /// </summary>
    public static (MenuItem? Dish, string? Reason) Offer(Venue venue, string name, int price,
                                                        int effort = 1, IEnumerable<Need>? needs = null)
    {
        ArgumentNullException.ThrowIfNull(venue);

        if (string.IsNullOrWhiteSpace(name)) return (null, "菜总得有个名字");
        if (price < 0) return (null, "价钱不能是负的");
        if (effort < 1) return (null, "一道菜至少要花一点工夫");
        if (venue.Dish(name.Trim()) is not null) return (null, "菜单上已经有「" + name.Trim() + "」了");

        var dish = new MenuItem
        {
            Name = name.Trim(),
            Price = price,
            Effort = effort,
            Needs = (needs ?? Array.Empty<Need>()).ToList()
        };
        venue.Menu.Add(dish);
        return (dish, null);
    }

    public static (StockItem? Item, string? Reason) Supply(Venue venue, Character? payer, string item,
                                                          int count, int unitCost = 0)
    {
        ArgumentNullException.ThrowIfNull(venue);

        if (string.IsNullOrWhiteSpace(item)) return (null, "进货总得说是进什么");
        if (count <= 0) return (null, "进货总得进一点");
        if (unitCost < 0) return (null, "进价不能是负的");

        var cost = unitCost * count;
        if (payer is not null && payer.Attributes.Reserve < cost)
            return (null, $"{payer.Id} 的余力不够付这笔 {cost}");

        if (payer is not null && cost > 0)
            payer.Attributes.Reserve = Attributes.Clamp(payer.Attributes.Reserve - cost);

        var line = venue.Stock.FirstOrDefault(s => string.Equals(s.Name, item.Trim(), StringComparison.Ordinal));
        if (line is null)
        {
            line = new StockItem { Name = item.Trim(), Count = count };
            venue.Stock.Add(line);
        }
        else
        {
            line.Count += count;
        }

        if (cost > 0)
        {
            venue.Ledger.Add(new LedgerLine
            {
                Kind = LedgerKind.Supply,
                Amount = -cost,
                Note = $"{item.Trim()} ×{count}",
                WhoId = payer?.Id
            });
        }

        return (line, null);
    }

    /// <summary>
    /// Hires someone. Only a supporting character can hold a job here: a principal is a resident
    /// of the house, and letting one be staff would quietly take a stage seat without saying so.
    /// </summary>
    public static (Staff? Staff, string? Reason) Hire(Venue venue, World world, string staffId,
                                                     int wage = 0, string role = "店员")
    {
        ArgumentNullException.ThrowIfNull(venue);
        ArgumentNullException.ThrowIfNull(world);

        var who = world.Find(staffId);
        if (who is null) return (null, "没有这个角色：" + staffId);
        if (who.Tier != SeatTier.Supporting)
            return (null, $"{staffId} 是主角色，占着台上一个席位，不能同时当店员");
        if (venue.Staff.Any(s => s.CharacterId == who.Id)) return (null, $"{who.Id} 已经在这儿做了");
        if (wage < 0) return (null, "工钱不能是负的");

        var job = new Staff { CharacterId = who.Id, Role = string.IsNullOrWhiteSpace(role) ? "店员" : role, Wage = wage };
        venue.Staff.Add(job);
        return (job, null);
    }

    public static bool Dismiss(Venue venue, string staffId) =>
        venue.Staff.RemoveAll(s => s.CharacterId == staffId) > 0;

    /// <summary>
    /// One guest, one dish. Everything that can go wrong is checked before anything changes, so
    /// a refused order leaves the larder and the purse exactly where they were.
    /// </summary>
    public static (Receipt? Receipt, string? Reason) Serve(World world, Venue venue, Character guest,
                                                          string dishName, Character? server = null)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(venue);
        ArgumentNullException.ThrowIfNull(guest);

        if (!venue.Open) return (null, $"{venue.Name}还关着门");

        var dish = venue.Dish(dishName);
        if (dish is null) return (null, $"菜单上没有「{dishName}」");

        if (server is not null && !venue.Staff.Any(s => s.CharacterId == server.Id && s.OnDuty))
            return (null, $"{server.Id} 不在这家店里当值");

        foreach (var need in dish.Needs)
            if (venue.StockOf(need.Item) < need.Count)
                return (null, $"「{dish.Name}」要用 {need.Item}，库里只剩 {venue.StockOf(need.Item)}");

        if (guest.Attributes.Reserve < dish.Price)
            return (null, $"{guest.Id} 的余力付不起 {dish.Price}");

        // Pay first, then the larder: a guest who cannot pay must not eat.
        guest.Attributes.Reserve = Attributes.Clamp(guest.Attributes.Reserve - dish.Price);
        var owner = world.Find(venue.OwnerId);
        if (owner is not null)
            owner.Attributes.Reserve = Attributes.Clamp(owner.Attributes.Reserve + dish.Price);

        foreach (var need in dish.Needs)
        {
            var line = venue.Stock.First(s => string.Equals(s.Name, need.Item, StringComparison.Ordinal));
            line.Count -= need.Count;
        }

        // Eating settles a person: the point of buying something is that you come out of it
        // steadier than you went in, or nobody would ever spend the reserve.
        guest.Attributes.Strain = Attributes.Clamp(guest.Attributes.Strain - 3);
        guest.Attributes.Poise = Attributes.Clamp(guest.Attributes.Poise + 1);

        if (owner is not null && server is not null && server.Id != owner.Id)
            owner.Attributes.ShiftBond(server.Id, +1);

        venue.Ledger.Add(new LedgerLine
        {
            Kind = LedgerKind.Sale,
            Amount = dish.Price,
            Note = dish.Name,
            WhoId = guest.Id
        });

        return (new Receipt
        {
            VenueId = venue.Id,
            Dish = dish.Name,
            Price = dish.Price,
            GuestId = guest.Id,
            ServedById = server?.Id,
            Effects = new AttributeDelta(1, 0, 0, -3, -dish.Price)
        }, null);
    }

    /// <summary>
    /// Pays everyone who worked. Wages come out of the till, and a till that cannot cover them
    /// records the shortfall instead of pretending the evening was free.
    /// </summary>
    public static (int Paid, int Short, string? Reason) PayWages(World world, Venue venue)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(venue);

        var paid = 0;
        var shortfall = 0;

        foreach (var job in venue.Staff.Where(s => s.OnDuty && s.Wage > 0))
        {
            if (venue.Balance < job.Wage) { shortfall += job.Wage; continue; }

            var who = world.Find(job.CharacterId);
            if (who is null) continue;

            who.Attributes.Reserve = Attributes.Clamp(who.Attributes.Reserve + job.Wage);
            venue.Ledger.Add(new LedgerLine
            {
                Kind = LedgerKind.Wage,
                Amount = -job.Wage,
                Note = job.Role,
                WhoId = job.CharacterId
            });
            paid += job.Wage;
        }

        return (paid, shortfall, shortfall > 0 ? $"账上不够发工钱，差了 {shortfall}" : null);
    }
}
