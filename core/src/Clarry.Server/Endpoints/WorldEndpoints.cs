namespace Clarry.Server;

using Clarry.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

/// <summary>
/// The world as seen from outside: what it is, who is on stage, and what the public face of each
/// seated character looks like.
///
/// Almost everything here is a read. The one write is the world's own name, genre and premise --
/// not anybody's situation, and not a scene. A theatre that cannot say what city it stands in has
/// to leave those fields empty forever, and an empty premise is not neutrality, it is an omission.
/// </summary>
internal static class WorldEndpoints
{
    public static void MapWorldEndpoints(this WebApplication app, ClarryApp ctx)
    {
        app.MapGet("/api/health", () => Results.Ok(new
        {
            ok = true,
            service = "clarry",
            stage = ctx.World.Stage.Count,
            capacity = StageLimits.MaxActiveAgents,
            round = ctx.World.CurrentRound?.Number ?? 0,
            dice = ctx.World.Dice.Enabled,
            driven = ctx.Loop.DrivenIds.Count,
            looping = ctx.Loop.Running,
            loaded = ctx.Loaded,
            file = ctx.Store.Path
        }));

        app.MapGet("/api/world", () => Results.Ok(new
        {
            ctx.World.Id,
            ctx.World.Title,
            ctx.World.Genre,
            ctx.World.Premise,
            registry = ctx.World.Registry.Count,
            stage = ctx.World.Stage.Active,
            capacity = ctx.World.Stage.Capacity,
            scenes = new
            {
                open = ctx.World.Scenes.Count(s => s.Open),
                closed = ctx.World.Scenes.Count(s => !s.Open),
                occupied = ctx.World.OccupiedCount,
                room = ctx.World.RoomLeft,
                active = ctx.World.Active.Id
            },
            rounds = ctx.World.Rounds.Count,
            turns = ctx.World.History.Count,
            clues = new { ctx.World.CluesResolved, ctx.World.ClueGoal },
            wishes = new
            {
                left = ctx.World.WishesLeft,
                budget = ctx.World.WishBudget,
                granted = ctx.World.Wishes.Select(w => w.Note)
            },
            forced = new { a = ctx.World.ForcedPairA, b = ctx.World.ForcedPairB },
            dice = new { ctx.World.Dice.Enabled, ctx.World.Dice.Notation }
        }));

        // Two different limits, on purpose. The sixty-four is how many residents may be active at
        // once -- beyond that a scene stops being readable. Staff are bounded by nothing except
        // how many a venue needs to feel staffed, which is a completely different number.
        app.MapGet("/api/tiers", () => Results.Ok(new
        {
            capacity = StageLimits.MaxActiveAgents,
            seated = ctx.World.Stage.Count,
            principals = ctx.World.Registry.Values.Count(c => c.Tier == SeatTier.Principal),
            supporting = ctx.World.Registry.Values.Count(c => c.Tier == SeatTier.Supporting),
            residents = ctx.World.Registry.Values
                .Where(c => c.Tier == SeatTier.Principal)
                .Select(c => new { c.Id, c.Name, seated = ctx.World.Stage.Active.Contains(c.Id) }),
            staff = ctx.World.Registry.Values
                .Where(c => c.Tier == SeatTier.Supporting)
                .Select(c => new { c.Id, c.Name, c.Sheet.Role })
        }));

        // The public face of every seated character: enough for the stage to draw a card, and
        // nothing that a Probe would have to be spent to learn.
        app.MapGet("/api/cast", () => Results.Ok(
            ctx.World.Stage.Active
                .Select(id => ctx.World.Find(id))
                .Where(c => c is not null)
                .Select(c => new
                {
                    c!.Id,
                    c.Name,
                    c.IsPlayer,
                    role = c.Sheet.Role,
                    summary = c.Sheet.Summary,
                    voice = c.Sheet.Voice,
                    traits = c.Sheet.Traits,
                    traitsNow = c.Traits.Weights,
                    traitsShowing = c.Traits.Describe(),
                    portrait = $"/assets/portraits/{c.Id}.png",
                    // The face is decided by the settlement, not by the viewer. This field only
                    // names which file to try; the stage falls back to the neutral portrait when
                    // the file is missing, which is what everyone looks like until variants exist.
                    expression = ctx.World.Faces.GetValueOrDefault(c.Id, Expression.Neutral).ToString().ToLowerInvariant(),
                    expressionText = ExpressionRules.Text(ctx.World.Faces.GetValueOrDefault(c.Id, Expression.Neutral)),
                    // Which image to show is a list, most specific first. The stage walks it and
                    // stops at the first file that loads, so an undrawn close-up falls back to
                    // the pixel portrait instead of leaving a hole where a character should be.
                    //
                    // The list deliberately contains files that may not exist: whether a name has
                    // been drawn is a fact about this checkout, not about the rules, and putting
                    // a disk check here would make /api/cast answer differently on two machines
                    // holding the same world. The client is the one that tries, and it can see
                    // its own 404s.
                    shot = ctx.World.Shots.GetValueOrDefault(c.Id, Shot.Wide).ToString().ToLowerInvariant(),
                    shotText = ShotRules.Text(ctx.World.Shots.GetValueOrDefault(c.Id, Shot.Wide)),
                    faces = ShotRules.Candidates(
                        c.Id,
                        ctx.World.Faces.GetValueOrDefault(c.Id, Expression.Neutral),
                        ctx.World.Shots.GetValueOrDefault(c.Id, Shot.Wide)),
                    face = ShotRules.Candidates(
                        c.Id,
                        ctx.World.Faces.GetValueOrDefault(c.Id, Expression.Neutral),
                        ctx.World.Shots.GetValueOrDefault(c.Id, Shot.Wide))[0],
                    tier = c.Tier.ToString().ToLowerInvariant(),
                    driven = ctx.Loop.DrivenIds.Contains(c.Id),
                    kind = c.Agent.Kind.ToString().ToLowerInvariant(),
                    a = new
                    {
                        poise = c.Attributes.Poise,
                        insight = c.Attributes.Insight,
                        renown = c.Attributes.Renown,
                        strain = c.Attributes.Strain,
                        reserve = c.Attributes.Reserve,
                        ember = c.Attributes.Ember,
                        bond = c.Attributes.Bond,
                        names = c.Attributes.Names
                    }
                })));
        // Who has ever caught themselves being watched, and what they said about it.
        //
        // Short on purpose. Most worlds answer with an empty list, and a long run answers with
        // two or three entries; a richer shape would suggest there is more to see, and there
        // never is. Nothing here says who was watching, because nobody in the house can know.
        app.MapGet("/api/glimpses", () => Results.Ok(new
        {
            count = ctx.World.Glimpses.Count,
            seen = ctx.World.Glimpses.Values
                .OrderBy(g => g.At)
                .Select(g => new
                {
                    g.CharacterId,
                    g.SceneId,
                    g.Round,
                    g.At,
                    delivered = g.ToldAt is not null,
                    g.Said
                })
        }));

        // Puts the event in front of one person on their next turn. This exists for the
        // acceptance suite and for anyone who wants to watch it happen once -- waiting for a
        // one-in-two-thousand coincidence is not a test, it is a hope.
        //
        // It does not weaken the rule it is standing in for: somebody who has already noticed
        // cannot be forced again, so the one-shot property survives the shortcut.
        app.MapPost("/api/glimpses/force", async (EnterRequest r) =>
        {
            if (ctx.World.Find(r.Id) is null) return Results.NotFound(new { error = "没有这个角色：" + r.Id });
            if (ctx.World.Glimpses.ContainsKey(r.Id))
                return Results.Conflict(new { error = r.Id + " 已经察觉过一次了，不会再有第二次" });

            var scene = ctx.World.Scenes.FirstOrDefault(s => s.Open && s.Cast.Active.Contains(r.Id));
            if (scene is null) return Results.BadRequest(new { error = r.Id + " 不在任何开着的幕里，没机会停下来" });

            await ctx.Gate.WaitAsync();
            try
            {
                ctx.World.Glimpses[r.Id] = new Glimpse
                {
                    CharacterId = r.Id,
                    SceneId = scene.Id,
                    Round = scene.CurrentRound?.Number ?? 0
                };
                ctx.Persist();
            }
            finally { ctx.Gate.Release(); }

            return Results.Ok(new
            {
                forced = r.Id,
                scene = scene.Id,
                note = "下一轮问到这个人时，他会在自己的 prompt 里读到那一段"
            });
        });

        // The city's own name, genre and premise. Not anybody's situation and not a scene: this
        // is where the theatre stands, and it is the only thing an outside caller may write here
        // without touching a resident. Every field is optional -- a caller that only wants to
        // name the city should not have to restate its premise to avoid blanking it.
        app.MapPost("/api/world", async (WorldMetaRequest r) =>
        {
            await ctx.Gate.WaitAsync();
            try
            {
                if (r.Title is not null) ctx.World.Title = r.Title.Trim();
                if (r.Genre is not null) ctx.World.Genre = r.Genre.Trim();
                if (r.Premise is not null) ctx.World.Premise = r.Premise.Trim();
                ctx.Persist();
                return Results.Ok(new
                {
                    ctx.World.Title,
                    ctx.World.Genre,
                    ctx.World.Premise
                });
            }
            finally { ctx.Gate.Release(); }
        });
    }
}

/// <summary>
/// What the world is called and what kind of place it is. All three are optional because a
/// premise is long and a caller fixing a typo in the title should not have to carry it along.
/// </summary>
record WorldMetaRequest(string? Title, string? Genre, string? Premise);
