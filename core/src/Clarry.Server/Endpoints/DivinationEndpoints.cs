namespace Clarry.Server;

using Clarry.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

/// <summary>
/// What somebody owns, and what they can read off it.
///
/// Three levels, and they are levels rather than labels. A plot is ground: a back garden is one of
/// them, and it is not a building. A basic building stands on a plot. An extension has no ground
/// of its own and is added to a basic building -- which is why an observatory cannot be bought on
/// an empty lot, and why building takes a plot or a building but never both.
///
/// The sky above all of it is shared: one weather, one season, one night, above every scene at
/// once. That is exactly why hearing about it from someone else is worth something, and why an
/// astrologer can be paid for knowing which way it is turning.
/// </summary>
internal static class DivinationEndpoints
{
    public static void MapDivinationEndpoints(this WebApplication app, ClarryApp ctx)
    {
        app.MapPost("/api/carry", async (CarryRequest r) =>
        {
            var c = ctx.World.Find(r.Id);
            if (c is null) return Results.NotFound(new { error = "没有这个角色：" + r.Id });
            if (!ApparatusText.TryParse(r.Kind, out var kind))
                return Results.BadRequest(new { error = "没有这种器材：" + r.Kind });

            Carried? kit;
            string? reason;
            await ctx.Gate.WaitAsync();
            try
            {
                (kit, reason) = Astrologer.Carry(c, kind, r.From);
                if (kit is not null) ctx.Persist();
            }
            finally { ctx.Gate.Release(); }

            if (kit is null) return Results.Conflict(new { error = reason });

            return Results.Ok(new
            {
                carried = kit.Id,
                kind = kit.Kind.ToString().ToLowerInvariant(),
                label = ApparatusText.Of(kit.Kind),
                housing = "carried",
                housingLabel = ApparatusRules.HousingText(Housing.Carried),
                astrologer = Astrologer.IsAstrologer(c),
                missing = Astrologer.Missing(c).Select(ApparatusText.Of)
            });
        });

        // More ground inside a building that already exists: a cellar, a roof, whatever comes next.
        // A basic building brings one plot with it (a workshop brings a backyard), so this is for
        // adding to that, not for conjuring the first one.
        app.MapPost("/api/plot", async (PlotRequest r) =>
        {
            var c = ctx.World.Find(r.Id);
            if (c is null) return Results.NotFound(new { error = "没有这个角色：" + r.Id });
            if (!PlotText.TryParse(r.Kind, out var kind))
                return Results.BadRequest(new { error = "没有这种场地：" + r.Kind });

            Plot? plot;
            string? reason;
            await ctx.Gate.WaitAsync();
            try
            {
                (plot, reason) = Astrologer.AddPlot(c, r.BuildingId, kind, r.Name);
                if (plot is not null) ctx.Persist();
            }
            finally { ctx.Gate.Release(); }

            if (plot is null) return Results.Conflict(new { error = reason });

            return Results.Ok(new
            {
                plot = plot.Id,
                kind = plot.Kind.ToString().ToLowerInvariant(),
                label = plot.Label,
                housing = "ground",
                housingLabel = ApparatusRules.HousingText(Housing.Ground)
            });
        });

        app.MapPost("/api/build", async (BuildRequest r) =>
        {
            var c = ctx.World.Find(r.Id);
            if (c is null) return Results.NotFound(new { error = "没有这个角色：" + r.Id });
            if (!ApparatusText.TryParse(r.Kind, out var kind))
                return Results.BadRequest(new { error = "没有这种建筑：" + r.Kind });

            Building? built;
            string? reason;
            await ctx.Gate.WaitAsync();
            try
            {
                (built, reason) = Astrologer.Build(c, kind, r.OnPlot);
                if (built is not null) ctx.Persist();
            }
            finally { ctx.Gate.Release(); }

            if (built is null) return Results.Conflict(new { error = reason });

            return Results.Ok(new
            {
                built = built.Id,
                kind = built.Kind.ToString().ToLowerInvariant(),
                label = ApparatusText.Of(built.Kind),
                housing = built.Housing.ToString().ToLowerInvariant(),
                housingLabel = ApparatusRules.HousingText(built.Housing),
                onPlot = built.OnPlot,
                // A basic building answers with the ground it came with; an extension has none of
                // its own.
                plots = built.Plots.Select(p => new { p.Id, label = p.Label }),
                standing = Astrologer.Standing(c, built),
                astrologer = Astrologer.IsAstrologer(c),
                missing = Astrologer.Missing(c).Select(ApparatusText.Of)
            });
        });

        app.MapGet("/api/sky", () => Results.Ok(new
        {
            weather = ctx.World.Sky.Weather,
            season = ctx.World.Sky.Season,
            night = ctx.World.Sky.Night,
            forecastBy = ctx.World.Sky.ForecastBy,
            at = ctx.World.Sky.At,
            text = ctx.World.Sky.Describe()
        }));

        app.MapPost("/api/sky", async (SkyRequest r) =>
        {
            await ctx.Gate.WaitAsync();
            try
            {
                if (!string.IsNullOrWhiteSpace(r.Weather)) ctx.World.Sky.Weather = r.Weather!;
                if (!string.IsNullOrWhiteSpace(r.Season)) ctx.World.Sky.Season = r.Season!;
                if (r.Night is { } n) ctx.World.Sky.Night = Math.Clamp(n, 0, 100);
                ctx.World.Sky.At = DateTimeOffset.UtcNow;
                ctx.Persist();
            }
            finally { ctx.Gate.Release(); }

            return Results.Ok(new
            {
                weather = ctx.World.Sky.Weather,
                season = ctx.World.Sky.Season,
                text = ctx.World.Sky.Describe()
            });
        });

        // A reading by hand, for the audience and for tests: the same code path the resolver uses
        // during a round, so there is only ever one definition of what a reading does.
        app.MapPost("/api/divine", async (DivineRequest r) =>
        {
            var reader = ctx.World.Find(r.ById);
            if (reader is null) return Results.NotFound(new { error = "没有这个角色：" + r.ById });
            if (!ApparatusText.TryParse(r.Kind, out var tool))
                return Results.BadRequest(new { error = "没有这种器材：" + r.Kind });

            DivinationOutcome outcome;
            await ctx.Gate.WaitAsync();
            try
            {
                var querent = r.TargetId is null ? null : ctx.World.Find(r.TargetId);
                var board = new RoundResult { Round = ctx.World.CurrentRound?.Number ?? 0 };
                outcome = Divination.Perform(ctx.World, board, reader, tool, querent, r.Question, r.Advice);

                if (outcome.Ok)
                {
                    foreach (var line in board.Revealed)
                        ctx.World.Record(reader.Id, "narration", line, r.TargetId);
                    foreach (var note in board.Notes)
                        ctx.World.Record(reader.Id, "narration", note, null);
                    ctx.Persist();
                }
            }
            finally { ctx.Gate.Release(); }

            if (!outcome.Ok) return Results.Conflict(new { error = outcome.Reason });

            return Results.Ok(new
            {
                by = r.ById,
                tool = r.Kind.ToLowerInvariant(),
                outcome.Reading,
                outcome.Proficiency,
                outcome.Unlocked,
                outcome.Earned,
                outcome.Advice,
                outcome.Weather,
                price = Astrologer.Price(reader),
                paidFrom = Astrologer.Livelihood
            });
        });

        app.MapGet("/api/astrologers", () => Results.Ok(new
        {
            astrologers = ctx.World.Registry.Values
                .Where(Astrologer.IsAstrologer)
                .Select(c => new
                {
                    c.Id,
                    proficiency = Astrologer.Proficiency(c),
                    price = Astrologer.Price(c)
                }),
            sky = new
            {
                ctx.World.Sky.Weather,
                ctx.World.Sky.Season,
                ctx.World.Sky.Night,
                ctx.World.Sky.ForecastBy
            }
        }));
    }
}

// --- requests ------------------------------------------------------------------------
record CarryRequest(string Id, string Kind, string? From);
record PlotRequest(string Id, string BuildingId, string Kind, string? Name);
record BuildRequest(string Id, string Kind, string? OnPlot);
record SkyRequest(string? Weather, string? Season, int? Night);
record DivineRequest(string ById, string Kind, string? TargetId, string? Question, string? Advice);
