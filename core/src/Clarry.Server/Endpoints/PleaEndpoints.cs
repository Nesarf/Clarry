namespace Clarry.Server;

using Clarry.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

/// <summary>
/// Requests and the places they are left at.
///
/// A resident has enough autonomy that the evening must be able to finish without anyone
/// watching. So being stuck is never a wall: it becomes a request, and a request has three exits
/// -- another character answers, a shrine answers, or nobody comes and the asker goes another way.
/// Only the middle one needs an audience, and it is off by default.
///
/// The two answering roads are separate endpoints on purpose. Answering by a person is a promise
/// that still has to be handed over; answering by a shrine is the world deciding, and it records
/// a god rather than whoever was watching. That absence is the fourth wall.
/// </summary>
internal static class PleaEndpoints
{
    public static void MapPleaEndpoints(this WebApplication app, ClarryApp ctx)
    {
        app.MapGet("/api/pleas", () => Results.Ok(new
        {
            open = ctx.World.Pleas.Where(p => p.State == PleaState.Open).Select(Describe),
            answered = ctx.World.Pleas.Where(p => p.State == PleaState.Answered).TakeLast(20).Select(Describe),
            lapsed = ctx.World.Pleas.Where(p => p.State == PleaState.Lapsed).TakeLast(20).Select(Describe)
        }));

        app.MapPost("/api/plea", async (PleadRequest r) =>
        {
            var who = ctx.World.Find(r.FromId);
            if (who is null) return Results.NotFound(new { error = "没有这个角色：" + r.FromId });
            if (!BlockedText.TryParse(r.Blocked, out var blocked))
                return Results.BadRequest(new { error = "没有这种缺口：" + r.Blocked });
            if (string.IsNullOrWhiteSpace(r.Needs))
                return Results.BadRequest(new { error = "求什么总得说清" });
            if (r.ToId is not null && ctx.World.Find(r.ToId) is null)
                return Results.NotFound(new { error = "要指名的人不存在：" + r.ToId });

            Plea plea;
            await ctx.Gate.WaitAsync();
            try
            {
                plea = Pleas.Open(ctx.World, who, blocked, r.Needs, r.Text ?? "", r.ToId,
                                  r.Lifetime ?? Pleas.DefaultLifetime, r.ShrineId);
                ctx.World.Record(who.Id, "ask", plea.Text, r.ToId);
                ctx.Persist();
            }
            finally { ctx.Gate.Release(); }

            return Results.Ok(Describe(plea));
        });

        app.MapPost("/api/plea/answer", async (AnswerPleaRequest r) =>
        {
            var by = ctx.World.Find(r.ById);
            if (by is null) return Results.NotFound(new { error = "没有这个角色：" + r.ById });

            Plea? plea;
            string? reason;
            await ctx.Gate.WaitAsync();
            try
            {
                (plea, reason) = Pleas.Answer(ctx.World, r.PleaId, by);
                if (plea is not null)
                {
                    ctx.World.Record(by.Id, "answer", $"应下 {plea.FromId} 的请求：{plea.Needs}", plea.FromId);
                    ctx.Persist();
                }
            }
            finally { ctx.Gate.Release(); }

            if (plea is null) return Results.Conflict(new { error = reason });
            return Results.Ok(Describe(plea));
        });

        // The other road: a shrine answers. Recorded as the god over that shrine, never as
        // whoever was watching -- the world has no way to say "the audience did this", and that
        // absence is the wall.
        app.MapPost("/api/plea/grant", async (GrantPleaRequest r) =>
        {
            var plea = ctx.World.Pleas.FirstOrDefault(p => p.Id == r.PleaId);
            if (plea is null) return Results.NotFound(new { error = "没有这条请求：" + r.PleaId });
            if (plea.State != PleaState.Open)
                return Results.Conflict(new { error = "这条请求已经" + (plea.State == PleaState.Answered ? "有人应了" : "过期了") });

            var shrine = ShrineRules.Pick(ctx.World, plea.ShrineId);
            if (shrine is null) return Results.Conflict(new { error = "这条请求没有落在任何一处地方" });

            var deity = ShrineRules.DeityOf(ctx.World, shrine);
            if (deity is null) return Results.Conflict(new { error = "那处地方上面没有神" });

            string line;
            await ctx.Gate.WaitAsync();
            try
            {
                plea.State = PleaState.Answered;
                plea.AnsweredBy = deity.Id;
                deity.Answered++;

                line = ShrineRules.AnsweredLine(plea, shrine, deity);
                ctx.World.Record("旁白", "narration", line, plea.FromId);
                ctx.Persist();
            }
            finally { ctx.Gate.Release(); }

            return Results.Ok(new
            {
                plea = plea.Id,
                shrine = shrine.Name,
                deity = deity.Name,
                deity.Asked,
                deity.Answered,
                line
            });
        });

        // --- shrines and gods -----------------------------------------------------------------
        // A request needs somewhere to be left, and something for it to be left to. Both are
        // world objects with names, because a character has to be able to say what it did and to
        // whom.

        app.MapGet("/api/shrines", () => Results.Ok(new
        {
            shrines = ctx.World.Shrines.Select(s =>
            {
                var god = ctx.World.Deities.FirstOrDefault(d => d.Id == s.DeityId);
                return new
                {
                    s.Id,
                    s.Name,
                    s.VenueId,
                    deityId = s.DeityId,
                    deityName = god?.Name,
                    domain = god?.Domain,
                    temper = god?.Temper,
                    asked = god?.Asked ?? 0,
                    answered = god?.Answered ?? 0,
                    open = ctx.World.Pleas.Count(p => p.State == PleaState.Open && p.ShrineId == s.Id)
                };
            })
        }));

        app.MapPost("/api/shrine", async (ShrineRequest r) =>
        {
            Deity deity;
            if (!string.IsNullOrWhiteSpace(r.DeityName))
            {
                deity = new Deity
                {
                    Id = "deity-" + (ctx.World.Deities.Count + 1),
                    Name = r.DeityName.Trim(),
                    Domain = string.IsNullOrWhiteSpace(r.Domain) ? "说不清" : r.Domain.Trim(),
                    Temper = string.IsNullOrWhiteSpace(r.Temper) ? "沉默" : r.Temper.Trim()
                };
            }
            else
            {
                deity = ctx.World.Deities.FirstOrDefault() ?? ShrineRules.DefaultDeity();
            }

            Shrine? shrine;
            string? reason;
            await ctx.Gate.WaitAsync();
            try
            {
                (shrine, reason) = ShrineRules.Raise(ctx.World, r.Name, deity, r.VenueId);
                if (shrine is not null) ctx.Persist();
            }
            finally { ctx.Gate.Release(); }

            if (shrine is null) return Results.Conflict(new { error = reason });
            return Results.Ok(new
            {
                shrine = shrine.Id,
                shrine.Name,
                deity = deity.Id,
                deityName = deity.Name,
                deity.Domain,
                deity.Temper
            });
        });
    }

    private static object Describe(Plea p) => new
    {
        p.Id,
        p.FromId,
        p.ToId,
        blocked = p.Blocked.ToString().ToLowerInvariant(),
        blockedText = BlockedText.Of(p.Blocked),
        p.Needs,
        p.Text,
        p.Lifetime,
        state = p.State.ToString().ToLowerInvariant(),
        p.AnsweredBy,
        p.At
    };
}

// --- requests ------------------------------------------------------------------------
record PleadRequest(string FromId, string Blocked, string Needs, string? Text, string? ToId, int? Lifetime, string? ShrineId);
record AnswerPleaRequest(string PleaId, string ById);
record GrantPleaRequest(string PleaId);
record ShrineRequest(string Name, string? DeityName, string? Domain, string? Temper, string? VenueId);
