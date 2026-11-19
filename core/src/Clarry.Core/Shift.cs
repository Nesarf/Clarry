using System.Text.Json.Serialization;

namespace Clarry.Core;

/// <summary>Someone standing in line. What they want is a dish name, or nothing -- and nothing means
/// "surprise me", which is an easier guest to serve and a less satisfying one.</summary>
public sealed class WaitingGuest
{
    public required string CharacterId { get; init; }
    public string? Want { get; init; }
    public DateTimeOffset ArrivedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// One evening of business. A shift is a queue, not a round of simultaneous declarations: people
/// arrive one at a time, get served one at a time, and the only thing that can pile up is the
/// line. That is why nothing in here needs the theatre's round machinery -- there is nothing to
/// synchronise.
///
/// Not saved. A shift is what is happening right now, like a round: the venue, its books, its
/// menu and its reputation survive a restart, the people standing in line do not.
/// </summary>
public sealed class Shift
{
    public required string VenueId { get; init; }
    public bool Open { get; set; } = true;

    public List<WaitingGuest> Queue { get; init; } = new();
    public List<string> Served { get; init; } = new();
    public List<string> WalkedOut { get; init; } = new();

    /// <summary>Takings booked during this shift, as the books moved.</summary>
    public int BalanceAtOpen { get; init; }
    public int ReputationAtOpen { get; init; }

    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? EndedAt { get; set; }
}

/// <summary>What one shift came to. Read by the owner, by the audience, and by the next shift.</summary>
public sealed class ShiftReport
{
    public required string VenueId { get; init; }
    public int Served { get; init; }
    public int WalkedOut { get; init; }
    public int Takings { get; init; }
    public int ReputationBefore { get; init; }
    public int ReputationAfter { get; init; }
    public List<string> Notes { get; init; } = new();
}

/// <summary>
/// Running one evening. Every step is an event, not a turn -- arrive, serve, turn away, close --
/// and every step either happens or explains why it did not.
///
/// Reputation is the whole feedback loop, and it is deliberately a double edge. A good name brings
/// more people, and more people means a longer line, and a line longer than the staff can serve is
/// how a good evening turns into a bad name. There is no way to win a shift and no way to lose one:
/// a dish that cannot be made is one guest who leaves, not a defeat.
/// </summary>
public static class Shifts
{
    /// <summary>How many guests one person on duty can look after before the line matters.</summary>
    public const int QueuePerStaff = 2;

    /// <summary>Floor and ceiling for reputation, so the curve never runs away in either direction.</summary>
    public static int Cap(int reputation) => Math.Clamp(reputation, 0, 100);

    /// <summary>
    /// How many people will wait. Staff set the real limit; a good name only adds a little
    /// patience on top, which is why hiring is a decision about capacity and not about flavour.
    /// </summary>
    public static int Capacity(Venue venue)
    {
        ArgumentNullException.ThrowIfNull(venue);
        return Math.Max(1, venue.OnDutyCount) * QueuePerStaff + venue.Reputation / 20;
    }

    public static (Shift? Shift, string? Reason) Open(Venue venue)
    {
        ArgumentNullException.ThrowIfNull(venue);

        if (!venue.Open) return (null, $"{venue.Name}还没开门");
        if (venue.Shift is { Open: true }) return (null, "这一班还在开，先打一次烊");
        if (venue.Menu.Count == 0) return (null, "菜单是空的，开门也没东西可出");

        var shift = new Shift
        {
            VenueId = venue.Id,
            BalanceAtOpen = venue.Balance,
            ReputationAtOpen = venue.Reputation
        };
        venue.Shift = shift;
        return (shift, null);
    }

    /// <summary>
    /// Someone walks in. A full line does not refuse them politely -- they look, and they leave,
    /// and the name takes the damage. That is the cost of being popular without being staffed.
    /// </summary>
    public static (WaitingGuest? Guest, string? Reason, int Capacity) Arrive(World world, Venue venue,
                                                                           string characterId, string? want = null)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(venue);

        var capacity = Capacity(venue);
        var shift = venue.Shift;
        if (shift is null || !shift.Open) return (null, "现在没有开班", capacity);

        var who = world.Find(characterId);
        if (who is null) return (null, "没有这个角色：" + characterId, capacity);
        if (who.Id == venue.OwnerId) return (null, "老板不用排队", capacity);
        if (shift.Queue.Any(g => g.CharacterId == characterId))
            return (null, $"{characterId} 已经在队里了", capacity);

        if (shift.Queue.Count >= capacity)
        {
            shift.WalkedOut.Add(characterId);
            venue.Reputation = Cap(venue.Reputation - 1);
            return (null, $"队排满了（{capacity} 人），{characterId} 看了一眼就走了", capacity);
        }

        var guest = new WaitingGuest { CharacterId = characterId, Want = want };
        shift.Queue.Add(guest);
        return (guest, null, capacity);
    }

    /// <summary>
    /// Serves whoever is at the front. The dish is the owner's choice when none is named; a guest
    /// who asked for something specific and got something else still pays, and is still fed --
    /// they simply do not become a regular.
    /// </summary>
    public static (Receipt? Receipt, string? Reason, int Reputation) ServeHead(World world, Venue venue,
                                                                              Character? server = null,
                                                                              string? dish = null)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(venue);

        var shift = venue.Shift;
        if (shift is null || !shift.Open) return (null, "现在没有开班", venue.Reputation);
        if (shift.Queue.Count == 0) return (null, "队里没人", venue.Reputation);

        var head = shift.Queue[0];
        var guest = world.Find(head.CharacterId);
        if (guest is null)
        {
            shift.Queue.RemoveAt(0);
            return (null, "队首的人已经不在这座城里了", venue.Reputation);
        }

        var wanted = dish ?? head.Want ?? venue.Menu.FirstOrDefault()?.Name;
        if (string.IsNullOrWhiteSpace(wanted))
            return (null, "菜单是空的，没什么可出", venue.Reputation);

        var (receipt, reason) = Trade.Serve(world, venue, guest, wanted, server);
        if (receipt is null)
        {
            // A dish that could not be made is one guest who leaves, not a lost evening.
            shift.Queue.RemoveAt(0);
            shift.WalkedOut.Add(guest.Id);
            venue.Reputation = Cap(venue.Reputation - 1);
            return (null, reason, venue.Reputation);
        }

        shift.Queue.RemoveAt(0);
        shift.Served.Add(guest.Id);

        // Did they get what they came for? A guest with no preference is easy to satisfy and
        // worth less to the name than one who asked and was answered.
        var credit = head.Want is null
            ? 1
            : string.Equals(head.Want, receipt.Dish, StringComparison.Ordinal) ? 2 : 0;
        venue.Reputation = Cap(venue.Reputation + credit);

        return (receipt, null, venue.Reputation);
    }

    /// <summary>Sends the front of the line away without serving them. Costs the name a little.</summary>
    public static (string? Guest, string? Reason, int Reputation) TurnAwayHead(Venue venue)
    {
        ArgumentNullException.ThrowIfNull(venue);

        var shift = venue.Shift;
        if (shift is null || !shift.Open) return (null, "现在没有开班", venue.Reputation);
        if (shift.Queue.Count == 0) return (null, "队里没人", venue.Reputation);

        var head = shift.Queue[0];
        shift.Queue.RemoveAt(0);
        shift.WalkedOut.Add(head.CharacterId);
        venue.Reputation = Cap(venue.Reputation - 1);
        return (head.CharacterId, null, venue.Reputation);
    }

    /// <summary>
    /// Closes the evening. Whoever is still standing there did not get served, and that is the
    /// number an owner should be looking at when they decide whether to hire someone.
    /// </summary>
    public static (ShiftReport? Report, string? Reason) Close(Venue venue)
    {
        ArgumentNullException.ThrowIfNull(venue);

        var shift = venue.Shift;
        if (shift is null || !shift.Open) return (null, "现在没有开班");

        shift.Open = false;
        shift.EndedAt = DateTimeOffset.UtcNow;
        venue.DaysOpen++;

        var stuck = shift.Queue.Count;
        foreach (var left in shift.Queue) shift.WalkedOut.Add(left.CharacterId);
        shift.Queue.Clear();
        if (stuck > 0) venue.Reputation = Cap(venue.Reputation - stuck);

        var notes = new List<string>();
        if (stuck > 0) notes.Add($"打烊时还有 {stuck} 个人没等到");
        if (shift.WalkedOut.Count > 0) notes.Add($"这一班一共走了 {shift.WalkedOut.Count} 个人");
        if (shift.Served.Count == 0) notes.Add("一单也没做成");

        var report = new ShiftReport
        {
            VenueId = venue.Id,
            Served = shift.Served.Count,
            WalkedOut = shift.WalkedOut.Count,
            Takings = venue.Balance - shift.BalanceAtOpen,
            ReputationBefore = shift.ReputationAtOpen,
            ReputationAfter = venue.Reputation,
            Notes = notes
        };

        // A shift that is over is a report, not a state. Leaving it attached would make every
        // later read of the venue describe an evening that ended hours ago.
        venue.Shift = null;
        return (report, null);
    }
}
