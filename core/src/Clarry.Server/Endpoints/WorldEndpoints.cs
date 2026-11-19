namespace Clarry.Server;

using Clarry.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

/// <summary>
/// The world as seen from outside: what it is, who is on stage, and what the public face of each
/// seated character looks like. Read-only -- nothing here changes the world, which is why this
/// was the safest group to move out of Program.cs first.
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
    }
}
