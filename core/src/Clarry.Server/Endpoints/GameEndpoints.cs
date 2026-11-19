namespace Clarry.Server;

using Clarry.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

/// <summary>
/// Four interludes, one shape.
///
/// A mini-game is something the characters sit down to play, never the engine of the story. Every
/// one of them takes the same three steps here: pick who is playing, play it, apply what it
/// decided. The applying is done in the endpoint rather than inside the game so that a game class
/// stays a pure function of its inputs -- which is what makes them testable without a world.
///
/// They differ in what they decide. Civ-like and the duel and the board produce a winner; the
/// vigil produces a shared held-or-not and no winner at all, because a night nobody wins is the
/// point of it.
/// </summary>
internal static class GameEndpoints
{
    public static void MapGameEndpoints(this WebApplication app, ClarryApp ctx)
    {
        app.MapPost("/api/minigame/civ-like", async () =>
        {
            await ctx.Gate.WaitAsync();
            try
            {
                var players = ctx.World.Stage.Active
                    .Select(id => ctx.World.Find(id))
                    .Where(c => c is not null)
                    .Select(c => c!)
                    .ToList();

                var game = new CivLikeGame();
                if (!game.CanStart(ctx.World, players))
                    return Results.Conflict(new { error = "这个小游戏需要 2 到 4 位在台上的角色" });

                var result = game.Play(ctx.World, players);
                foreach (var (id, delta) in result.Effects)
                {
                    var c = ctx.World.Find(id);
                    if (c is null) continue;
                    c.Attributes.Renown = Attributes.Clamp(c.Attributes.Renown + delta.Renown);
                    c.Attributes.Strain = Attributes.Clamp(c.Attributes.Strain + delta.Strain);
                    c.Attributes.Reserve = Attributes.Clamp(c.Attributes.Reserve + delta.Reserve);
                    ctx.World.Record(id, "game", "围着一张小棋盘下了一局，胜者是 " + result.WinnerId, null);
                }
                ctx.Persist();

                return Results.Ok(new
                {
                    game = result.GameId,
                    winner = result.WinnerId,
                    usedDice = result.UsedDice,
                    effects = result.Effects,
                    log = result.Log
                });
            }
            finally { ctx.Gate.Release(); }
        });

        // A two-player card duel. Pass ?a=&b= to pick the seats, or let it take the first two on
        // stage. The shuffle is seeded from the world, so the same pairing deals the same hand.
        app.MapPost("/api/minigame/card-duel", async (string? a, string? b) =>
        {
            await ctx.Gate.WaitAsync();
            try
            {
                List<Character> players;
                if (a is not null && b is not null)
                {
                    var ca = ctx.World.Find(a);
                    var cb = ctx.World.Find(b);
                    if (ca is null || cb is null) return Results.NotFound(new { error = "没有这个角色" });
                    if (ca.Id == cb.Id) return Results.BadRequest(new { error = "得是两个人" });
                    players = new List<Character> { ca, cb };
                }
                else
                {
                    players = ctx.World.Stage.Active
                        .Select(id => ctx.World.Find(id))
                        .Where(c => c is not null)
                        .Select(c => c!)
                        .Take(2)
                        .ToList();
                }

                var game = new CardDuelGame();
                if (!game.CanStart(ctx.World, players))
                    return Results.Conflict(new { error = "黑白对局需要正好两个人" });

                var result = game.Play(ctx.World, players);
                foreach (var (id, delta) in result.Effects)
                {
                    var c = ctx.World.Find(id);
                    if (c is null) continue;
                    c.Attributes.Insight = Attributes.Clamp(c.Attributes.Insight + delta.Insight);
                    c.Attributes.Renown = Attributes.Clamp(c.Attributes.Renown + delta.Renown);
                    c.Attributes.Strain = Attributes.Clamp(c.Attributes.Strain + delta.Strain);
                    c.Attributes.Reserve = Attributes.Clamp(c.Attributes.Reserve + delta.Reserve);
                    ctx.World.Record(id, "game", $"打了一局黑白对局，{result.WinnerId} 胜", null);
                }
                ctx.Persist();

                return Results.Ok(new
                {
                    game = result.GameId,
                    winner = result.WinnerId,
                    usedDice = result.UsedDice,
                    players = players.Select(p => p.Id),
                    effects = result.Effects,
                    log = result.Log
                });
            }
            finally { ctx.Gate.Release(); }
        });

        // The co-operative interlude: keep a watch until morning. There is no winner -- the result
        // is a shared held-or-not, and the effects land on everyone the same way.
        app.MapPost("/api/minigame/vigil", async () =>
        {
            await ctx.Gate.WaitAsync();
            try
            {
                var players = ctx.World.Stage.Active
                    .Select(id => ctx.World.Find(id))
                    .Where(c => c is not null)
                    .Select(c => c!)
                    .Take(4)
                    .ToList();

                var game = new VigilGame();
                if (!game.CanStart(ctx.World, players))
                    return Results.Conflict(new { error = "守夜需要 2 到 4 个人在台上" });

                var result = game.Play(ctx.World, players);
                foreach (var (id, delta) in result.Effects)
                {
                    var c = ctx.World.Find(id);
                    if (c is null) continue;
                    c.Attributes.Poise = Attributes.Clamp(c.Attributes.Poise + delta.Poise);
                    c.Attributes.Renown = Attributes.Clamp(c.Attributes.Renown + delta.Renown);
                    c.Attributes.Strain = Attributes.Clamp(c.Attributes.Strain + delta.Strain);
                    c.Attributes.Reserve = Attributes.Clamp(c.Attributes.Reserve + delta.Reserve);
                    ctx.World.Record(id, "game",
                        result.TeamSucceeded == true ? "一起守到了天亮" : "守夜没撑住", null);
                }
                ctx.Persist();

                return Results.Ok(new
                {
                    game = result.GameId,
                    teamSucceeded = result.TeamSucceeded,
                    players = players.Select(p => p.Id),
                    usedDice = result.UsedDice,
                    effects = result.Effects,
                    log = result.Log
                });
            }
            finally { ctx.Gate.Release(); }
        });

        app.MapPost("/api/minigame/board", async () =>
        {
            await ctx.Gate.WaitAsync();
            try
            {
                var players = ctx.World.Stage.Active
                    .Select(id => ctx.World.Find(id))
                    .Where(c => c is not null)
                    .Select(c => c!)
                    .Take(4)
                    .ToList();

                var game = new BoardGame();
                if (!game.CanStart(ctx.World, players))
                    return Results.Conflict(new { error = "大富翁需要 2 到 4 个人在台上" });

                var result = game.Play(ctx.World, players);
                foreach (var (id, delta) in result.Effects)
                {
                    var c = ctx.World.Find(id);
                    if (c is null) continue;
                    c.Attributes.Renown = Attributes.Clamp(c.Attributes.Renown + delta.Renown);
                    c.Attributes.Strain = Attributes.Clamp(c.Attributes.Strain + delta.Strain);
                    c.Attributes.Reserve = Attributes.Clamp(c.Attributes.Reserve + delta.Reserve);
                    ctx.World.Record(id, "game", $"打了一局大富翁，{result.WinnerId} 胜", null);
                }
                ctx.Persist();

                return Results.Ok(new
                {
                    game = result.GameId,
                    winner = result.WinnerId,
                    usedDice = result.UsedDice,
                    players = players.Select(p => p.Id),
                    effects = result.Effects,
                    log = result.Log
                });
            }
            finally { ctx.Gate.Release(); }
        });
    }
}
