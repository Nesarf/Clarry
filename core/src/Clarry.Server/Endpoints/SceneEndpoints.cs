namespace Clarry.Server;

using Clarry.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

/// <summary>
/// The scenes: opening them, closing them, and the camera that decides which one is on screen.
///
/// This is the group where the theatre's structure shows most plainly. A world holds several
/// stretches of play at once; each owns its cast, rounds, history and clue progress; the people
/// and their relationships outlive all of them. Sixty-four is the whole world's cap on residents
/// busy at once, not one scene's.
/// </summary>
internal static class SceneEndpoints
{
    public static void MapSceneEndpoints(this WebApplication app, ClarryApp ctx)
    {
        // Who would share a scene, and why. Deterministic by design: the same world always
        // proposes the same staging, so an audience coming back finds the people it left sitting
        // together.
        app.MapGet("/api/scene/propose", () => Results.Ok(
            ScenePicker.Propose(ctx.World).Select(p => new
            {
                cast = p.Cast,
                reason = p.Reason,
                solo = p.IsSolo
            })));

        app.MapGet("/api/scenes", () => Results.Ok(new
        {
            open = ctx.World.Scenes.Where(s => s.Open).Select(Describe),
            closed = ctx.World.Scenes.Where(s => !s.Open).Select(Describe),
            occupied = ctx.World.OccupiedCount,
            capacity = StageLimits.MaxActiveAgents,
            room = ctx.World.RoomLeft
        }));

        app.MapPost("/api/scenes/open", async (SceneOpenRequest r) =>
        {
            await ctx.Gate.WaitAsync();
            try
            {
                var (scene, reason) = ctx.World.OpenScene(r.Cast ?? Array.Empty<string>(), r.Title ?? "");
                if (scene is null) return Results.Conflict(new { error = reason });
                ctx.Persist();
                return Results.Ok(new
                {
                    opened = scene.Id,
                    number = scene.Number,
                    cast = scene.Cast.Active,
                    occupied = ctx.World.OccupiedCount
                });
            }
            finally { ctx.Gate.Release(); }
        });

        app.MapPost("/api/scenes/close", async (string? sceneId) =>
        {
            await ctx.Gate.WaitAsync();
            try
            {
                var id = string.IsNullOrWhiteSpace(sceneId) ? ctx.World.Active.Id : sceneId;
                var closed = ctx.World.CloseScene(id);
                if (closed) ctx.Persist();
                return Results.Ok(new { closed = id, wasOpen = closed, occupied = ctx.World.OccupiedCount });
            }
            finally { ctx.Gate.Release(); }
        });

        // Closes every open scene, then casts afresh from the pairing rule. This is the step that
        // turns ScenePicker's proposals into actual parallel scenes instead of a report nobody
        // acts on.
        app.MapPost("/api/scenes/auto", async () =>
        {
            await ctx.Gate.WaitAsync();
            try
            {
                foreach (var s in ctx.World.Scenes.Where(s => s.Open).ToList()) ctx.World.CloseScene(s.Id);

                // Only residents are cast; staff belong to a venue, not to a scene.
                var free = ctx.World.Registry.Values
                    .Where(c => c.Tier == SeatTier.Principal)
                    .Select(c => c.Id)
                    .ToList();

                var opened = new List<object>();
                var refused = new List<object>();

                foreach (var proposal in ScenePicker.Propose(ctx.World, free))
                {
                    var (scene, reason) = ctx.World.OpenScene(proposal.Cast, proposal.Reason);
                    if (scene is null) refused.Add(new { cast = proposal.Cast, reason });
                    else opened.Add(new { scene.Id, scene.Number, cast = scene.Cast.Active, reason = proposal.Reason });
                }

                ctx.Persist();
                return Results.Ok(new
                {
                    opened,
                    refused,
                    occupied = ctx.World.OccupiedCount,
                    capacity = StageLimits.MaxActiveAgents
                });
            }
            finally { ctx.Gate.Release(); }
        });

        // A new play clears the history, the round ledger and the clue progress. The cast stays:
        // characters keep whatever state the last play left them in.
        app.MapPost("/api/scene/new", async (int? clueGoal) =>
        {
            if (ctx.Loop.Running) return Results.Conflict(new { error = "循环正在跑，先收场再开新戏" });
            await ctx.Gate.WaitAsync();
            try
            {
                ctx.World.ResetScene(clueGoal);
                ctx.Persist();
                return Results.Ok(new
                {
                    opened = true,
                    clues = ctx.World.ClueGoal,
                    stage = ctx.World.Stage.Active,
                    turns = ctx.World.History.Count,
                    rounds = ctx.World.Rounds.Count
                });
            }
            finally { ctx.Gate.Release(); }
        });

        // The other half of the same idea, and deliberately not the default: a new scene keeps
        // the people, a wiped world does not. Everything anyone did here is gone afterwards,
        // including who they were. The shrine, the gods and the sky stay, because those are the
        // place rather than the residents -- and a world with nowhere to leave a request would
        // not be emptied, it would be broken.
        //
        // Nothing in the acceptance suites calls this. Wiping is something a person decides,
        // not something a suite does on the way past.
        app.MapPost("/api/world/wipe", async () =>
        {
            if (ctx.Loop.Running) return Results.Conflict(new { error = "循环正在跑，先收场再清空" });
            await ctx.Gate.WaitAsync();
            try
            {
                var cleared = new
                {
                    characters = ctx.World.Registry.Count,
                    scenes = ctx.World.Scenes.Count,
                    turns = ctx.World.History.Count,
                    pleas = ctx.World.Pleas.Count,
                    venues = ctx.World.Venues.Count
                };
                ctx.World.Wipe();
                ctx.Persist();
                return Results.Ok(new
                {
                    wiped = true,
                    cleared,
                    left = new
                    {
                        characters = ctx.World.Registry.Count,
                        scenes = ctx.World.Scenes.Count,
                        active = ctx.World.Active.Id,
                        shrines = ctx.World.Shrines.Count,
                        deities = ctx.World.Deities.Count,
                        wishes = ctx.World.WishesLeft
                    }
                });
            }
            finally { ctx.Gate.Release(); }
        });

        // A line said to one named scene, not to whoever happens to be active. Narration has no
        // actor and no turn: it is the world remarking on a room, and it moves that room's camera
        // because a line of history is exactly what the audience is watching for.
        app.MapPost("/api/scene/narrate", async (NarrateRequest r) =>
        {
            var target = ctx.World.Scenes.FirstOrDefault(s => s.Id == r.SceneId);
            if (target is null) return Results.NotFound(new { error = "没有这一幕：" + r.SceneId });
            if (string.IsNullOrWhiteSpace(r.Text)) return Results.BadRequest(new { error = "总要有点话说" });

            await ctx.Gate.WaitAsync();
            try
            {
                target.Record(r.ActorId ?? "世界", "narration", r.Text, null);
                ctx.Persist();
            }
            finally { ctx.Gate.Release(); }

            return Results.Ok(new
            {
                scene = target.Id,
                lines = target.History.Count,
                touchedAt = target.LastChangedAt,
                watching = ctx.World.Focus()?.Id
            });
        });

        // --- the audience's camera -----------------------------------------------------------
        // Which scene is on screen. The default is "whatever moved last", because an audience
        // should not have to keep choosing; pinning exists for when they do want to keep watching
        // one room while something louder happens elsewhere.

        app.MapGet("/api/audience", () =>
        {
            var focused = ctx.World.Focus();

            return Results.Ok(new
            {
                watching = focused?.Id,
                title = focused?.Title,
                because = ctx.World.FocusBecause(),
                // Only a pin that still points at an open scene is a pin. One whose scene has
                // closed is history, and reporting it would suggest the audience is watching
                // something it is not.
                pinned = ctx.World.FocusScene is not null
                         && ctx.World.Scenes.Any(s => s.Id == ctx.World.FocusScene && s.Open)
                    ? ctx.World.FocusScene
                    : null,
                open = ctx.World.Scenes.Where(s => s.Open).Select(s => new
                {
                    s.Id,
                    s.Title,
                    s.Number,
                    s.LastChangedAt,
                    cast = s.Cast.Active,
                    lines = s.History.Count,
                    last = s.History.Count == 0 ? null : s.History[^1].Text,
                    watching = focused is not null && s.Id == focused.Id
                }),
                wishes = new { left = ctx.World.WishesLeft, budget = ctx.World.WishBudget }
            });
        });

        app.MapPost("/api/audience/follow", async (FollowRequest r) =>
        {
            var target = ctx.World.Scenes.FirstOrDefault(s => s.Id == r.SceneId && s.Open);
            if (target is null) return Results.NotFound(new { error = "没有开着这一幕：" + r.SceneId });

            await ctx.Gate.WaitAsync();
            try
            {
                ctx.World.FocusScene = target.Id;
                ctx.Persist();
            }
            finally { ctx.Gate.Release(); }

            return Results.Ok(new { watching = target.Id, because = ctx.World.FocusBecause() });
        });

        app.MapPost("/api/audience/auto", async () =>
        {
            await ctx.Gate.WaitAsync();
            try
            {
                ctx.World.FocusScene = null;
                ctx.Persist();
            }
            finally { ctx.Gate.Release(); }

            var focused = ctx.World.Focus();
            return Results.Ok(new { watching = focused?.Id, because = ctx.World.FocusBecause() });
        });

        // With several scenes running at once, "the history" is ambiguous. Named, it is one
        // room's evening; unnamed, it stays the active scene's, which is what every older caller
        // expects.
        app.MapGet("/api/history", (int? take, string? scene) =>
        {
            var n = Math.Clamp(take ?? 50, 1, 500);

            if (scene is null) return Results.Ok(ctx.World.History.TakeLast(n));

            var target = ctx.World.Scenes.FirstOrDefault(s => s.Id == scene);
            if (target is null) return Results.NotFound(new { error = "没有这一幕：" + scene });
            return Results.Ok(target.History.TakeLast(n));
        });
    }

    private static object Describe(Scene s) => new
    {
        s.Id,
        s.Number,
        s.Title,
        cast = s.Cast.Active,
        rounds = s.Rounds.Count,
        clues = new { s.CluesResolved, s.ClueGoal },
        s.Open,
        s.OpenedAt,
        s.ClosedAt,
        last = s.History.Count == 0 ? null : s.History[^1].Text
    };
}

// --- requests ------------------------------------------------------------------------
record SceneOpenRequest(string[]? Cast, string? Title);
record FollowRequest(string SceneId);
record NarrateRequest(string SceneId, string Text, string? ActorId);
