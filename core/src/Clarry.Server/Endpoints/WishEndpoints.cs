namespace Clarry.Server;

using Clarry.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

/// <summary>
/// The audience's three wishes.
///
/// Two per scene, and no way to spend a character's attributes on them -- the cost is the wish
/// itself. It used to be four interventions and three scenes' worth; three of the four were
/// things a resident can do by itself, so what is left is what only somebody outside can do:
/// point the camera, answer a request already waiting, or stop watching.
///
/// The names here are the words a wish is granted with. A granted request never says a viewer
/// did it -- that sentence belongs to the shrine layer, and it names a god.
/// </summary>
internal static class WishEndpoints
{
    public static void MapWishEndpoints(this WebApplication app, ClarryApp ctx)
    {
        app.MapPost("/api/wish", async (WishRequest r) =>
        {
            await ctx.Gate.WaitAsync();
            try
            {
                if (ctx.World.WishesLeft <= 0)
                    return Results.Conflict(new { error = "许愿池干了，这一场没有愿望了" });

                Wish wish;
                switch (r.Kind)
                {
                    case WishKind.Focus:
                    {
                        // The one wish that moves nothing but the camera. Watching a story is not
                        // only about what happens; it is also about which of the things happening
                        // you get to see.
                        if (string.IsNullOrWhiteSpace(r.SubjectId))
                            return Results.BadRequest(new { error = "得说清看哪一幕" });

                        var target = ctx.World.Scenes.FirstOrDefault(s => s.Id == r.SubjectId && s.Open);
                        if (target is null)
                            return Results.NotFound(new { error = "没有开着这一幕：" + r.SubjectId });

                        ctx.World.FocusScene = target.Id;
                        wish = Wish.Focus(target.Id);
                        break;
                    }

                    case WishKind.Grant:
                    {
                        // The only intervention left, and it can only land on something already
                        // asked for. Answered through a shrine, so the world records a god and
                        // never a viewer.
                        if (string.IsNullOrWhiteSpace(r.SubjectId))
                            return Results.BadRequest(new { error = "得说清应哪一条请求" });

                        var plea = ctx.World.Pleas.FirstOrDefault(p => p.Id == r.SubjectId);
                        if (plea is null)
                            return Results.NotFound(new { error = "没有这条请求：" + r.SubjectId });
                        if (plea.State != PleaState.Open)
                            return Results.Conflict(new
                            {
                                error = "这条请求已经" + (plea.State == PleaState.Answered ? "有人应了" : "过期了")
                            });

                        var shrine = ShrineRules.Pick(ctx.World, plea.ShrineId);
                        var deity = shrine is null ? null : ShrineRules.DeityOf(ctx.World, shrine);
                        if (shrine is null || deity is null)
                            return Results.Conflict(new { error = "这条请求没有落在一处有神的地方" });

                        plea.State = PleaState.Answered;
                        plea.AnsweredBy = deity.Id;
                        deity.Answered++;
                        ctx.World.Record("旁白", "narration",
                            ShrineRules.AnsweredLine(plea, shrine, deity), plea.FromId);
                        wish = Wish.Granted(plea.Id, plea.FromId, deity.Name);
                        break;
                    }

                    case WishKind.Halt:
                    {
                        ctx.Loop.Stop("观众许愿让这一幕停下");
                        wish = Wish.Halt();
                        break;
                    }

                    default:
                        return Results.BadRequest(new { error = "不认识的愿望" });
                }

                ctx.World.WishesLeft--;
                ctx.World.Wishes.Add(wish);
                ctx.Persist();

                return Results.Ok(new
                {
                    granted = wish.Kind.ToString(),
                    note = wish.Note,
                    left = ctx.World.WishesLeft,
                    budget = ctx.World.WishBudget
                });
            }
            finally { ctx.Gate.Release(); }
        });
    }
}

// --- requests ------------------------------------------------------------------------
record WishRequest(WishKind Kind, string? SubjectId, string? TargetId);
