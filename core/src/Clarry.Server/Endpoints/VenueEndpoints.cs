namespace Clarry.Server;

using Clarry.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

/// <summary>
/// A place someone runs: opening it, stocking it, staffing it, serving guests, and the evening
/// that the serving turns into.
///
/// A shop is not a scene. It stays where it is while the camera moves, it keeps its own books,
/// and anyone may walk in. Everything here either happens or says why it did not; a half-served
/// order would be a data bug wearing a friendly face.
///
/// The shift half is a queue, not a round: people arrive one at a time and are served one at a
/// time, so there is nothing to synchronise and no round machinery anywhere in it.
/// </summary>
internal static class VenueEndpoints
{
    public static void MapVenueEndpoints(this WebApplication app, ClarryApp ctx)
    {
        app.MapGet("/api/venues", () => Results.Ok(new
        {
            venues = ctx.World.Venues.Select(v => new
            {
                v.Id,
                v.Name,
                v.OwnerId,
                kind = v.Kind.ToString().ToLowerInvariant(),
                v.Open,
                balance = v.Balance,
                dishes = v.Menu.Count,
                staff = v.OnDutyCount
            })
        }));

        app.MapGet("/api/venue/{id}", (string id) =>
        {
            var v = ctx.World.Venues.FirstOrDefault(x => x.Id == id);
            if (v is null) return Results.NotFound(new { error = "没有这家店：" + id });
            return Results.Ok(Describe(v));
        });

        app.MapPost("/api/venue", async (VenueOpenRequest r) =>
        {
            var owner = ctx.World.Find(r.OwnerId);
            if (owner is null) return Results.NotFound(new { error = "没有这个角色：" + r.OwnerId });
            if (!VenueText.TryParse(r.Kind ?? "cafe", out var kind))
                return Results.BadRequest(new { error = "没有这种店：" + r.Kind });

            Venue? venue;
            string? reason;
            await ctx.Gate.WaitAsync();
            try
            {
                (venue, reason) = Trade.Open(ctx.World, owner, r.Name, kind);
                if (venue is not null) ctx.Persist();
            }
            finally { ctx.Gate.Release(); }

            if (venue is null) return Results.Conflict(new { error = reason });
            return Results.Ok(Describe(venue));
        });

        app.MapPost("/api/venue/toggle", async (VenueToggleRequest r) =>
        {
            var v = ctx.World.Venues.FirstOrDefault(x => x.Id == r.VenueId);
            if (v is null) return Results.NotFound(new { error = "没有这家店：" + r.VenueId });

            await ctx.Gate.WaitAsync();
            try
            {
                v.Open = r.Open;
                ctx.Persist();
            }
            finally { ctx.Gate.Release(); }

            return Results.Ok(new { venue = v.Id, open = v.Open });
        });

        app.MapPost("/api/venue/menu", async (VenueMenuRequest r) =>
        {
            var v = ctx.World.Venues.FirstOrDefault(x => x.Id == r.VenueId);
            if (v is null) return Results.NotFound(new { error = "没有这家店：" + r.VenueId });

            MenuItem? dish;
            string? reason;
            await ctx.Gate.WaitAsync();
            try
            {
                var needs = (r.Needs ?? Array.Empty<NeedRequest>())
                    .Where(n => !string.IsNullOrWhiteSpace(n.Item))
                    .Select(n => new Need { Item = n.Item, Count = Math.Max(1, n.Count) });
                (dish, reason) = Trade.Offer(v, r.Name, r.Price, r.Effort ?? 1, needs);
                if (dish is not null) ctx.Persist();
            }
            finally { ctx.Gate.Release(); }

            if (dish is null) return Results.Conflict(new { error = reason });
            return Results.Ok(new
            {
                dish = dish.Name,
                dish.Price,
                dish.Effort,
                needs = dish.Needs.Select(n => new { n.Item, n.Count })
            });
        });

        app.MapPost("/api/venue/stock", async (VenueStockRequest r) =>
        {
            var v = ctx.World.Venues.FirstOrDefault(x => x.Id == r.VenueId);
            if (v is null) return Results.NotFound(new { error = "没有这家店：" + r.VenueId });

            var payer = r.PayerId is null ? null : ctx.World.Find(r.PayerId);
            if (r.PayerId is not null && payer is null)
                return Results.NotFound(new { error = "没有这个角色：" + r.PayerId });

            StockItem? item;
            string? reason;
            await ctx.Gate.WaitAsync();
            try
            {
                (item, reason) = Trade.Supply(v, payer, r.Item, r.Count, r.UnitCost ?? 0);
                if (item is not null) ctx.Persist();
            }
            finally { ctx.Gate.Release(); }

            if (item is null) return Results.Conflict(new { error = reason });
            return Results.Ok(new { venue = v.Id, item = item.Name, item.Count, balance = v.Balance });
        });

        app.MapPost("/api/venue/staff", async (VenueStaffRequest r) =>
        {
            var v = ctx.World.Venues.FirstOrDefault(x => x.Id == r.VenueId);
            if (v is null) return Results.NotFound(new { error = "没有这家店：" + r.VenueId });

            Staff? job;
            string? reason;
            await ctx.Gate.WaitAsync();
            try
            {
                (job, reason) = Trade.Hire(v, ctx.World, r.StaffId, r.Wage ?? 0, r.Role ?? "店员");
                if (job is not null) ctx.Persist();
            }
            finally { ctx.Gate.Release(); }

            if (job is null) return Results.Conflict(new { error = reason });
            return Results.Ok(new
            {
                venue = v.Id,
                staff = job.CharacterId,
                job.Role,
                job.Wage,
                job.OnDuty
            });
        });

        app.MapPost("/api/venue/order", async (VenueOrderRequest r) =>
        {
            var v = ctx.World.Venues.FirstOrDefault(x => x.Id == r.VenueId);
            if (v is null) return Results.NotFound(new { error = "没有这家店：" + r.VenueId });

            var guest = ctx.World.Find(r.GuestId);
            if (guest is null) return Results.NotFound(new { error = "没有这个角色：" + r.GuestId });

            var server = r.ServerId is null ? null : ctx.World.Find(r.ServerId);
            if (r.ServerId is not null && server is null)
                return Results.NotFound(new { error = "没有这个角色：" + r.ServerId });

            Receipt? receipt;
            string? reason;
            await ctx.Gate.WaitAsync();
            try
            {
                (receipt, reason) = Trade.Serve(ctx.World, v, guest, r.Dish, server);
                if (receipt is not null)
                {
                    ctx.World.Record(r.GuestId, "act", $"在{v.Name}要了一份{receipt.Dish}", server?.Id);
                    ctx.Persist();
                }
            }
            finally { ctx.Gate.Release(); }

            if (receipt is null) return Results.Conflict(new { error = reason });
            return Results.Ok(new
            {
                venue = receipt.VenueId,
                dish = receipt.Dish,
                receipt.Price,
                guest = receipt.GuestId,
                servedBy = receipt.ServedById,
                effects = receipt.Effects,
                balance = v.Balance
            });
        });

        app.MapPost("/api/venue/wages", async (VenueWagesRequest r) =>
        {
            var v = ctx.World.Venues.FirstOrDefault(x => x.Id == r.VenueId);
            if (v is null) return Results.NotFound(new { error = "没有这家店：" + r.VenueId });

            int paid;
            int shortfall;
            string? reason;
            await ctx.Gate.WaitAsync();
            try
            {
                (paid, shortfall, reason) = Trade.PayWages(ctx.World, v);
                ctx.Persist();
            }
            finally { ctx.Gate.Release(); }

            return Results.Ok(new
            {
                venue = v.Id,
                paid,
                shortfall,
                note = reason,
                balance = v.Balance
            });
        });

        // --- the shift: one evening of business ------------------------------------------
        // A queue, not a round. People arrive one at a time and are served one at a time, so
        // there is nothing to synchronise and no round machinery anywhere in here. Every step is
        // an event.

        app.MapGet("/api/venue/shift/{id}", (string id) =>
        {
            var v = ctx.World.Venues.FirstOrDefault(x => x.Id == id);
            if (v is null) return Results.NotFound(new { error = "没有这家店：" + id });
            if (v.Shift is null)
                return Results.Ok(new { venue = id, open = false, queue = Array.Empty<object>() });
            return Results.Ok(DescribeShift(v.Shift));
        });

        app.MapPost("/api/venue/shift/open", async (ShiftVenueRequest r) =>
        {
            var v = ctx.World.Venues.FirstOrDefault(x => x.Id == r.VenueId);
            if (v is null) return Results.NotFound(new { error = "没有这家店：" + r.VenueId });

            Shift? shift;
            string? reason;
            await ctx.Gate.WaitAsync();
            try
            {
                (shift, reason) = Shifts.Open(v);
                if (shift is not null) ctx.Persist();
            }
            finally { ctx.Gate.Release(); }

            if (shift is null) return Results.Conflict(new { error = reason });
            return Results.Ok(new
            {
                venue = v.Id,
                open = shift.Open,
                capacity = Shifts.Capacity(v),
                reputation = v.Reputation
            });
        });

        app.MapPost("/api/venue/shift/arrive", async (ShiftArriveRequest r) =>
        {
            var v = ctx.World.Venues.FirstOrDefault(x => x.Id == r.VenueId);
            if (v is null) return Results.NotFound(new { error = "没有这家店：" + r.VenueId });

            WaitingGuest? guest;
            string? reason;
            int capacity;
            await ctx.Gate.WaitAsync();
            try
            {
                (guest, reason, capacity) = Shifts.Arrive(ctx.World, v, r.GuestId, r.Want);
                if (guest is not null) ctx.Persist();
            }
            finally { ctx.Gate.Release(); }

            if (guest is null)
                return Results.Conflict(new { error = reason, capacity, reputation = v.Reputation });

            return Results.Ok(new
            {
                arrived = guest.CharacterId,
                want = guest.Want,
                inLine = v.Shift?.Queue.Count ?? 0,
                capacity,
                reputation = v.Reputation
            });
        });

        app.MapPost("/api/venue/shift/serve", async (ShiftServeRequest r) =>
        {
            var v = ctx.World.Venues.FirstOrDefault(x => x.Id == r.VenueId);
            if (v is null) return Results.NotFound(new { error = "没有这家店：" + r.VenueId });

            var server = r.ServerId is null ? null : ctx.World.Find(r.ServerId);
            if (r.ServerId is not null && server is null)
                return Results.NotFound(new { error = "没有这个角色：" + r.ServerId });

            Receipt? receipt;
            string? reason;
            int reputation;
            await ctx.Gate.WaitAsync();
            try
            {
                (receipt, reason, reputation) = Shifts.ServeHead(ctx.World, v, server, r.Dish);
                if (receipt is not null)
                {
                    ctx.World.Record(receipt.GuestId, "act", $"在{v.Name}吃了{receipt.Dish}", server?.Id);
                    ctx.Persist();
                }
            }
            finally { ctx.Gate.Release(); }

            if (receipt is null) return Results.Conflict(new { error = reason, reputation });
            return Results.Ok(new
            {
                venue = v.Id,
                dish = receipt.Dish,
                receipt.Price,
                guest = receipt.GuestId,
                servedBy = receipt.ServedById,
                effects = receipt.Effects,
                reputation,
                inLine = v.Shift?.Queue.Count ?? 0
            });
        });

        app.MapPost("/api/venue/shift/turn-away", async (ShiftVenueRequest r) =>
        {
            var v = ctx.World.Venues.FirstOrDefault(x => x.Id == r.VenueId);
            if (v is null) return Results.NotFound(new { error = "没有这家店：" + r.VenueId });

            string? guest;
            string? reason;
            int reputation;
            await ctx.Gate.WaitAsync();
            try
            {
                (guest, reason, reputation) = Shifts.TurnAwayHead(v);
                if (guest is not null) ctx.Persist();
            }
            finally { ctx.Gate.Release(); }

            if (guest is null) return Results.Conflict(new { error = reason, reputation });
            return Results.Ok(new { turnedAway = guest, reputation, inLine = v.Shift?.Queue.Count ?? 0 });
        });

        app.MapPost("/api/venue/shift/close", async (ShiftVenueRequest r) =>
        {
            var v = ctx.World.Venues.FirstOrDefault(x => x.Id == r.VenueId);
            if (v is null) return Results.NotFound(new { error = "没有这家店：" + r.VenueId });

            ShiftReport? report;
            string? reason;
            await ctx.Gate.WaitAsync();
            try
            {
                (report, reason) = Shifts.Close(v);
                if (report is not null) ctx.Persist();
            }
            finally { ctx.Gate.Release(); }

            if (report is null) return Results.Conflict(new { error = reason });
            return Results.Ok(report);
        });
    }

    private static object Describe(Venue v) => new
    {
        v.Id,
        v.Name,
        v.OwnerId,
        kind = v.Kind.ToString().ToLowerInvariant(),
        kindLabel = VenueText.Of(v.Kind),
        v.Open,
        plots = v.Plots.Select(p => new
        {
            p.Id,
            kind = p.Kind.ToString().ToLowerInvariant(),
            label = p.Label
        }),
        menu = v.Menu.Select(m => new
        {
            m.Name,
            m.Price,
            m.Effort,
            needs = m.Needs.Select(n => new { n.Item, n.Count })
        }),
        stock = v.Stock.Select(s => new { s.Name, s.Count }),
        staff = v.Staff.Select(s => new { s.CharacterId, s.Role, s.Wage, s.OnDuty }),
        balance = v.Balance,
        v.Reputation,
        v.DaysOpen,
        capacity = Shifts.Capacity(v),
        shift = v.Shift is null ? null : new
        {
            open = v.Shift.Open,
            inLine = v.Shift.Queue.Count,
            served = v.Shift.Served.Count,
            walkedOut = v.Shift.WalkedOut.Count
        },
        ledger = v.Ledger.TakeLast(20).Select(l => new
        {
            kind = l.Kind.ToString().ToLowerInvariant(),
            l.Amount,
            l.Note,
            l.WhoId,
            l.At
        })
    };

    private static object DescribeShift(Shift s) => new
    {
        s.VenueId,
        s.Open,
        queue = s.Queue.Select(g => new { g.CharacterId, g.Want, g.ArrivedAt }),
        s.Served,
        s.WalkedOut,
        s.StartedAt,
        s.EndedAt
    };
}

// --- requests ------------------------------------------------------------------------
record VenueOpenRequest(string OwnerId, string Name, string? Kind);
record VenueToggleRequest(string VenueId, bool Open);
record VenueMenuRequest(string VenueId, string Name, int Price, int? Effort, NeedRequest[]? Needs);
record NeedRequest(string Item, int Count);
record VenueStockRequest(string VenueId, string Item, int Count, int? UnitCost, string? PayerId);
record VenueStaffRequest(string VenueId, string StaffId, int? Wage, string? Role);
record VenueOrderRequest(string VenueId, string GuestId, string Dish, string? ServerId);
record VenueWagesRequest(string VenueId);
record ShiftVenueRequest(string VenueId);
record ShiftArriveRequest(string VenueId, string GuestId, string? Want);
record ShiftServeRequest(string VenueId, string? ServerId, string? Dish);
