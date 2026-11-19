namespace Clarry.Server;

using Clarry.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

/// <summary>
/// What goes on inside one person: what they call someone, what they remember, and what they
/// talk other people into playing.
///
/// Three things that look unrelated and are not. A nickname and an activity both only exist
/// because they reach the prompt, and a memory is the thing the prompt is built from. None of
/// them is decoration: naming is the cheapest way two characters read as close, and a game the
/// world refuses to interpret is what keeps the theatre from becoming an interpreter.
/// </summary>
internal static class AgentEndpoints
{
    public static void MapAgentEndpoints(this WebApplication app, ClarryApp ctx)
    {
        // What A calls B. A name the model never sees is a name it never says, so this has to
        // reach the prompt or it is only a database row. Naming someone warms both sides a
        // little, because naming is itself a move.
        app.MapPost("/api/address", async (AddressRequest r) =>
        {
            await ctx.Gate.WaitAsync();
            try
            {
                var from = ctx.World.Find(r.ActorId);
                var to = ctx.World.Find(r.TargetId);
                if (from is null || to is null) return Results.NotFound(new { error = "没有这个角色" });
                if (from.Id == to.Id) return Results.BadRequest(new { error = "不能给自已起称呼" });
                if (string.IsNullOrWhiteSpace(r.Nickname))
                    return Results.BadRequest(new { error = "称呼不能是空的" });

                var nickname = r.Nickname.Trim();
                if (nickname.Length > 16)
                    return Results.BadRequest(new { error = "称呼太长了，十六个字以内" });

                from.Attributes.Names[to.Id] = nickname;
                from.Attributes.ShiftBond(to.Id, 2);
                to.Attributes.ShiftBond(from.Id, 1);
                ctx.World.Record(from.Id, "speak", $"从此管 {to.Id} 叫「{nickname}」", to.Id);
                ctx.Persist();

                return Results.Ok(new
                {
                    from = from.Id,
                    to = to.Id,
                    nickname,
                    bond = from.Attributes.BondWith(to.Id)
                });
            }
            finally { ctx.Gate.Release(); }
        });

        // What one character still remembers, heaviest first. Two characters at the same event do
        // not come away with the same amount of it, so this is per-person by design.
        app.MapGet("/api/memory/{id}", (string id) =>
        {
            var c = ctx.World.Find(id);
            if (c is null) return Results.NotFound(new { error = "没有这个角色：" + id });

            var style = MemoryStyle.For(c);
            var kept = ctx.Memories.Recall(id, 50);

            return Results.Ok(new
            {
                agent = id,
                style = new
                {
                    decay = style.Decay,
                    salience = style.Salience,
                    capacity = style.Capacity,
                    recall = style.Recall
                },
                total = ctx.Memories.Count(id),
                kept = kept.Select(m => new
                {
                    m.Text,
                    m.AboutId,
                    weight = Math.Round(m.Weight, 1),
                    at = m.At
                })
            });
        });

        // Forgetting on purpose: one character's memory, or all of it. A new scene keeps memories
        // by default -- they are the one thing that is supposed to outlive a scene.
        app.MapPost("/api/memory/clear", (string? agentId) =>
        {
            var removed = ctx.Memories.Clear(agentId);
            return Results.Ok(new { cleared = agentId ?? "*", rows = removed });
        });

        // --- activities ---------------------------------------------------------------------
        // Characters invent these themselves: a drinking contest, a wager, a race, a séance. The
        // world records the rules, the moves and the verdict, then applies only the effects the
        // verdict declares. It never runs the rules -- that is what keeps this open-ended instead
        // of turning into an interpreter, and what makes a crooked judge a scene rather than a bug.

        app.MapGet("/api/activity", () => Results.Ok(ctx.World.Activities.Select(a => new
        {
            a.Id,
            a.Name,
            a.HostId,
            a.Rules,
            participants = a.Participants,
            moves = a.Moves,
            a.JudgedBy,
            a.Verdict,
            a.Settled,
            effects = a.Effects.Select(e => new { e.ActorId, e.Delta })
        })));

        app.MapPost("/api/activity/open", async (ActivityOpenRequest r) =>
        {
            await ctx.Gate.WaitAsync();
            try
            {
                if (string.IsNullOrWhiteSpace(r.Name) || string.IsNullOrWhiteSpace(r.Rules))
                    return Results.BadRequest(new { error = "活动得有名字，也得有规则" });
                if (ctx.World.Find(r.HostId) is null)
                    return Results.NotFound(new { error = "没有这个角色：" + r.HostId });

                var participants = (r.Participants ?? Array.Empty<string>())
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                if (participants.Count < 2)
                    return Results.BadRequest(new { error = "活动至少要两个人" });
                foreach (var id in participants)
                    if (ctx.World.Find(id) is null) return Results.NotFound(new { error = "没有这个角色：" + id });

                var activity = new Activity
                {
                    Id = "act-" + (ctx.World.Activities.Count + 1),
                    Name = r.Name.Trim(),
                    HostId = r.HostId,
                    Rules = r.Rules.Trim()
                };
                activity.Participants.AddRange(participants);
                ctx.World.Activities.Add(activity);
                ctx.World.Record(r.HostId, "activity", $"提出了一场「{activity.Name}」：{activity.Rules}", null);
                ctx.Persist();

                return Results.Ok(new { opened = activity.Id, name = activity.Name, participants });
            }
            finally { ctx.Gate.Release(); }
        });

        app.MapPost("/api/activity/move", async (ActivityMoveRequest r) =>
        {
            await ctx.Gate.WaitAsync();
            try
            {
                var activity = ctx.World.Activities.FirstOrDefault(a => a.Id == r.ActivityId);
                if (activity is null) return Results.NotFound(new { error = "没有这个活动：" + r.ActivityId });
                if (activity.Settled) return Results.Conflict(new { error = "这场活动已经结束了" });
                if (!activity.Participants.Contains(r.ActorId))
                    return Results.BadRequest(new { error = r.ActorId + " 不在参与者里" });
                if (string.IsNullOrWhiteSpace(r.Move))
                    return Results.BadRequest(new { error = "提交不能是空的" });

                activity.Moves[r.ActorId] = r.Move;
                ctx.Persist();

                return Results.Ok(new
                {
                    submitted = r.ActorId,
                    moves = activity.Moves.Count,
                    participants = activity.Participants.Count,
                    everyoneMoved = activity.EveryoneMoved()
                });
            }
            finally { ctx.Gate.Release(); }
        });

        app.MapPost("/api/activity/close", async (ActivityCloseRequest r) =>
        {
            await ctx.Gate.WaitAsync();
            try
            {
                var activity = ctx.World.Activities.FirstOrDefault(a => a.Id == r.ActivityId);
                if (activity is null) return Results.NotFound(new { error = "没有这个活动：" + r.ActivityId });
                if (activity.Settled) return Results.Conflict(new { error = "这场活动已经结束了" });
                if (string.IsNullOrWhiteSpace(r.Verdict))
                    return Results.BadRequest(new { error = "裁决不能是空的" });
                if (ctx.World.Find(r.JudgedBy) is null)
                    return Results.NotFound(new { error = "没有这个角色：" + r.JudgedBy });

                activity.JudgedBy = r.JudgedBy;
                activity.Verdict = r.Verdict.Trim();
                activity.Settled = true;
                activity.SettledAt = DateTimeOffset.UtcNow;

                foreach (var e in r.Effects ?? Array.Empty<ActivityEffectRequest>())
                {
                    var c = ctx.World.Find(e.ActorId);
                    if (c is null) continue;

                    c.Attributes.Poise = Attributes.Clamp(c.Attributes.Poise + e.Poise);
                    c.Attributes.Insight = Attributes.Clamp(c.Attributes.Insight + e.Insight);
                    c.Attributes.Renown = Attributes.Clamp(c.Attributes.Renown + e.Renown);
                    c.Attributes.Strain = Attributes.Clamp(c.Attributes.Strain + e.Strain);
                    c.Attributes.Reserve = Attributes.Clamp(c.Attributes.Reserve + e.Reserve);
                    if (e.Ember) c.Attributes.Ember = true;
                    if (e.BondTarget is not null) c.Attributes.ShiftBond(e.BondTarget, e.BondDelta);

                    activity.Effects.Add(new ActivityEffect(c.Id, new AttributeDelta(
                        c.Attributes.Poise, c.Attributes.Insight, c.Attributes.Renown,
                        c.Attributes.Strain, c.Attributes.Reserve, c.Attributes.Ember)));
                }

                ctx.World.Record(activity.HostId, "activity",
                    $"「{activity.Name}」的结果：{activity.Verdict}", null);
                ctx.Persist();

                return Results.Ok(new
                {
                    settled = activity.Id,
                    judgedBy = activity.JudgedBy,
                    verdict = activity.Verdict,
                    effects = activity.Effects.Select(x => new { x.ActorId, x.Delta })
                });
            }
            finally { ctx.Gate.Release(); }
        });
    }
}

// --- requests ------------------------------------------------------------------------
record AddressRequest(string ActorId, string TargetId, string Nickname);
record ActivityOpenRequest(string Name, string HostId, string Rules, string[]? Participants);
record ActivityMoveRequest(string ActivityId, string ActorId, string Move);
record ActivityCloseRequest(string ActivityId, string JudgedBy, string Verdict, ActivityEffectRequest[]? Effects);
record ActivityEffectRequest(string ActorId, int Poise, int Insight, int Renown, int Strain, int Reserve,
                             bool Ember, string? BondTarget, int BondDelta);
