namespace Clarry.Server;

using Clarry.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

/// <summary>
/// The round: opening it, declaring into it, and settling it by hand.
///
/// The driven road (<see cref="RoundLoop"/>) goes through the same resolver; what differs is only
/// who calls it. Both roads settle in one serial place, because a settled order can reach across
/// scenes and two settlements must not interleave.
/// </summary>
internal static class RoundEndpoints
{
    public static void MapRoundEndpoints(this WebApplication app, ClarryApp ctx)
    {
        app.MapPost("/api/round/begin", async () =>
        {
            await ctx.Gate.WaitAsync();
            try
            {
                var r = ctx.World.BeginRound();
                ctx.Persist();
                return Results.Ok(new { round = r.Number, open = !r.Closed });
            }
            catch (InvalidOperationException e) { return Results.Conflict(new { error = e.Message }); }
            finally { ctx.Gate.Release(); }
        });

        app.MapGet("/api/round/current", () =>
        {
            var r = ctx.World.CurrentRound;
            if (r is null) return Results.Ok(new { round = 0, open = false, orders = Array.Empty<object>() });
            return Results.Ok(new
            {
                round = r.Number,
                open = !r.Closed,
                orders = r.Orders.Select(o => new
                {
                    o.ActorId,
                    kind = o.Kind.ToString().ToLowerInvariant(),
                    kindLabel = OrderKindText.Of(o.Kind),
                    o.TargetId,
                    o.Text,
                    o.Cost
                })
            });
        });

        app.MapPost("/api/round/order", async (OrderRequest r) =>
        {
            await ctx.Gate.WaitAsync();
            try
            {
                var round = ctx.World.CurrentRound;
                if (round is null) return Results.Conflict(new { error = "还没有开始的回合，先开一轮" });
                if (round.Closed) return Results.Conflict(new { error = "这一轮已经关闭" });
                if (ctx.World.Find(r.ActorId) is null)
                    return Results.NotFound(new { error = "没有这个角色：" + r.ActorId });

                round.Add(new Order
                {
                    ActorId = r.ActorId,
                    Kind = r.Kind,
                    TargetId = r.TargetId,
                    Text = r.Text,
                    // Without the next two lines an answer arrives with no plea attached and an
                    // impression is quietly dropped -- both look exactly like working features.
                    PleaId = string.IsNullOrWhiteSpace(r.PleaId) ? null : r.PleaId.Trim(),
                    Impression = string.IsNullOrWhiteSpace(r.Impression) ? null : r.Impression.Trim(),
                    Sequence = round.Orders.Count
                });

                return Results.Ok(new { accepted = true, round = round.Number, orders = round.Orders.Count });
            }
            finally { ctx.Gate.Release(); }
        });

        app.MapPost("/api/round/resolve", async () =>
        {
            if (ctx.Loop.Running) return Results.Conflict(new { error = "循环正在驱动这个世界，先收场" });

            await ctx.Gate.WaitAsync();
            try
            {
                var round = ctx.World.CurrentRound;
                if (round is null) return Results.Conflict(new { error = "还没有回合" });
                if (round.Result is not null) return Results.Conflict(new { error = "这一轮已经结算过了" });

                round.Close();
                var result = RoundResolver.Resolve(round, ctx.World);
                MemoryRecorder.Record(round, ctx.World, ctx.Memories);

                // A manual settlement ages requests the same way a loop beat does, so the two
                // roads into "a round happened" cannot disagree about what expires.
                var lapsed = Pleas.Expire(ctx.World);

                ctx.World.TurnPointer = ctx.World.Stage.NextTurn(ctx.World.TurnPointer);
                ctx.Persist();

                return Results.Ok(new
                {
                    round = result.Round,
                    converged = result.Converged,
                    revealed = result.Revealed,
                    ignored = result.Ignored.Select(o => new
                    {
                        o.ActorId,
                        kind = o.Kind.ToString().ToLowerInvariant()
                    }),
                    notes = result.Notes,
                    deltas = result.Deltas,
                    lapsed,
                    stage = ctx.World.Stage.Active,
                    next = ctx.World.CurrentActor()?.Id
                });
            }
            finally { ctx.Gate.Release(); }
        });
    }
}

// --- requests ------------------------------------------------------------------------
record OrderRequest(string ActorId, OrderKind Kind, string? TargetId, string? Text, string? PleaId, string? Impression);
