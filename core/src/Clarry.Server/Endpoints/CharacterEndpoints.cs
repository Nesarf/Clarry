namespace Clarry.Server;

using Clarry.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

/// <summary>
/// The people: registering one, reading one, putting one on stage, and the two views that are
/// assembled from several heads at once -- what somebody holds, and who somebody is.
///
/// These were moved out of Program.cs unchanged apart from the closure variables becoming
/// <c>ctx.</c> properties. Moving is not rewriting: if a body changed, this file would be a
/// second place the behaviour lives, which is the problem it exists to fix.
/// </summary>
internal static class CharacterEndpoints
{
    public static void MapCharacterEndpoints(this WebApplication app, ClarryApp ctx)
    {
        app.MapPost("/api/character", async (Character c) =>
        {
            if (string.IsNullOrWhiteSpace(c.Id) || string.IsNullOrWhiteSpace(c.Name))
                return Results.BadRequest(new { error = "id 与 name 都不能为空" });

            await ctx.Gate.WaitAsync();
            try
            {
                ctx.World.Add(c);
                ctx.Persist();
            }
            finally { ctx.Gate.Release(); }

            return Results.Ok(new { added = c.Id, registry = ctx.World.Registry.Count });
        });

        // One character, in full. The prompt is built from exactly these fields, so this is also
        // the honest way to check that something a model is supposed to see actually reached it.
        app.MapGet("/api/character/{id}", (string id) =>
        {
            var c = ctx.World.Find(id);
            if (c is null) return Results.NotFound(new { error = "没有这个角色：" + id });

            return Results.Ok(new
            {
                c.Id,
                c.Name,
                c.IsPlayer,
                tier = c.Tier.ToString().ToLowerInvariant(),
                c.Sheet,
                attributes = new
                {
                    c.Attributes.Poise, c.Attributes.Insight, c.Attributes.Renown,
                    c.Attributes.Strain, c.Attributes.Reserve, c.Attributes.Ember,
                    bond = c.Attributes.Bond, names = c.Attributes.Names
                },
                traits = c.Traits.Describe(),
                carried = c.Carried.Select(k => new
                {
                    k.Id,
                    kind = k.Kind.ToString().ToLowerInvariant(),
                    label = ApparatusText.Of(k.Kind)
                }),
                plots = c.Buildings.SelectMany(b => b.Plots.Select(p => new
                {
                    p.Id,
                    owner = b.Id,
                    kind = p.Kind.ToString().ToLowerInvariant(),
                    label = p.Label
                })),
                buildings = c.Buildings.Select(b => new
                {
                    b.Id,
                    kind = b.Kind.ToString().ToLowerInvariant(),
                    label = ApparatusText.Of(b.Kind),
                    housing = b.Housing.ToString().ToLowerInvariant(),
                    housingLabel = ApparatusRules.HousingText(b.Housing),
                    b.OnPlot,
                    standing = Astrologer.Standing(c, b),
                    plots = b.Plots.Select(p => new { p.Id, label = p.Label })
                }),
                skills = c.Skills,
                advice = c.Advice.Select(a => new { a.FromId, a.Text, a.Heeded }),
                astrologer = Astrologer.IsAstrologer(c),
                proficiency = Astrologer.Proficiency(c)
            });
        });

        app.MapPost("/api/stage/enter", async (EnterRequest r) =>
        {
            var candidate = ctx.World.Find(r.Id);
            if (candidate is null) return Results.NotFound(new { error = "没有这个角色：" + r.Id });
            if (candidate.Tier == SeatTier.Supporting)
                return Results.BadRequest(new { error = "店员不占台位，六十四席只留给主角色" });

            Stage.EnterResult result;
            await ctx.Gate.WaitAsync();
            try
            {
                result = ctx.World.Stage.Enter(r.Id);
                if (result.Entered) ctx.Persist();
            }
            finally { ctx.Gate.Release(); }

            return result.Entered
                ? Results.Ok(new { entered = r.Id, stage = ctx.World.Stage.Active, count = ctx.World.Stage.Count, capacity = ctx.World.Stage.Capacity })
                : Results.Conflict(new { error = result.Reason, count = ctx.World.Stage.Count, capacity = ctx.World.Stage.Capacity });
        });

        app.MapPost("/api/stage/leave", async (EnterRequest r) =>
        {
            bool left;
            await ctx.Gate.WaitAsync();
            try
            {
                left = ctx.World.Stage.Leave(r.Id);
                if (left) ctx.Persist();
            }
            finally { ctx.Gate.Release(); }

            return Results.Ok(new { left = r.Id, removed = left, count = ctx.World.Stage.Count });
        });

        // Everything one character holds, level by level. Reading it this way makes an
        // inconsistency visible instead of silent: a building whose plot is gone shows up as
        // standing=false rather than quietly disappearing from the list.
        app.MapGet("/api/holdings/{id}", (string id) =>
        {
            var c = ctx.World.Find(id);
            if (c is null) return Results.NotFound(new { error = "没有这个角色：" + id });

            return Results.Ok(new
            {
                owner = id,
                carried = c.Carried.Select(k => new
                {
                    k.Id,
                    kind = k.Kind.ToString().ToLowerInvariant(),
                    label = ApparatusText.Of(k.Kind),
                    housingLabel = ApparatusRules.HousingText(Housing.Carried)
                }),
                plots = c.Buildings.SelectMany(b => b.Plots.Select(p => new
                {
                    p.Id,
                    insideBuilding = b.Id,
                    label = p.Label,
                    extensions = c.Buildings.Count(x => x.OnPlot == p.Id)
                })),
                buildings = c.Buildings.Select(b => new
                {
                    b.Id,
                    kind = b.Kind.ToString().ToLowerInvariant(),
                    label = ApparatusText.Of(b.Kind),
                    housing = b.Housing.ToString().ToLowerInvariant(),
                    housingLabel = ApparatusRules.HousingText(b.Housing),
                    b.OnPlot,
                    standing = Astrologer.Standing(c, b)
                }),
                missing = Astrologer.Missing(c).Select(ApparatusText.Of),
                astrologer = Astrologer.IsAstrologer(c),
                proficiency = Astrologer.Proficiency(c),
                price = Astrologer.Price(c),
                paidFrom = Astrologer.Livelihood
            });
        });

        // Who somebody is, assembled from every head that has met them.
        //
        // Three layers, and only two of them are stored: what she says about herself, what each
        // other person wrote about her, and -- never stored anywhere -- what she thinks they
        // think. The third is the interesting one, and it is deliberately not a field: a person
        // cannot read it, so neither can a character, and the not-knowing is where trouble lives.
        app.MapGet("/api/who-is/{id}", (string id) =>
        {
            var c = ctx.World.Find(id);
            if (c is null) return Results.NotFound(new { error = "没有这个角色：" + id });

            var impressions = ctx.World.Registry.Values
                .Where(other => other.Id != id && other.Attributes.ImpressionOf(id) is not null)
                .Select(other => new
                {
                    byId = other.Id,
                    byName = other.Name,
                    text = other.Attributes.ImpressionOf(id),
                    warmth = other.Attributes.BondWith(id)
                })
                .ToList();

            return Results.Ok(new
            {
                id = c.Id,
                name = c.Name,
                selfImage = c.Sheet.SelfImage,
                role = c.Sheet.Role,
                summary = c.Sheet.Summary,
                impressions,
                // How many of the people who could have an opinion actually have one. A small
                // number is not a gap in the data; it is somebody nobody has looked at yet.
                heardFrom = impressions.Count,
                couldHaveHeard = Math.Max(0, ctx.World.Registry.Count - 1)
            });
        });
    }
}

// --- requests ------------------------------------------------------------------------
record EnterRequest(string Id);
