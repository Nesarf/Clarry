using Clarry.Core;
using Clarry.Server;
using Microsoft.Extensions.FileProviders;
using System.Text.Json.Serialization;

// Clarry server: the theatre's HTTP surface. Any client -- this DSH plugin, a Unity
// stage, a bare curl in a shell -- drives the same World through these endpoints.
// The world is loaded from disk at start and saved after every mutation.
//
// Two ways to advance the story:
//   manual  -- /api/round/begin, /api/round/order, /api/round/resolve, one call at a time
//   driven  -- /api/driver/attach then /api/loop/start, and the theatre runs itself
// Both take the same gate, so they never interleave.
//
// Everything a caller can read back -- error text included -- is written in the language
// the audience reads. Identifiers, routes and enum values stay as they are.

// The story is UTF-8 from end to end. A Windows console defaults to the system code page,
// which turns every character name in the log into question marks -- so pin the output here.
Console.OutputEncoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

var builder = WebApplication.CreateBuilder(args);
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.WriteIndented = true;
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase));
});
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));
var app = builder.Build();
app.UseCors();

var root = Environment.GetEnvironmentVariable("CLARRY_ROOT") ?? @"D:\Clarry";
var dataDir = Environment.GetEnvironmentVariable("CLARRY_DATA") ?? Path.Combine(root, "data");
var stagePage = Path.Combine(root, "stage", "index.html");
var assetsDir = Path.Combine(root, "assets");
Directory.CreateDirectory(assetsDir);
var store = new WorldStore(Path.Combine(dataDir, "world.json"));
var snapshot = store.Load();
var world = snapshot is null ? new World { Id = "main", Title = "" } : WorldStore.Restore(snapshot);

// Memory lives in SQLite rather than the world file: it is queried by relevance, never read
// whole, and it grows much faster than the world does.
var memories = new MemoryStore(Path.Combine(dataDir, "memories.db"));

// One gate for every mutation: a loop tick and a manual call take turns.
var gate = new SemaphoreSlim(1, 1);
var loop = new RoundLoop(world, store, memories, gate);

void Persist() => store.Save(world);

// Portraits are plain files; the stage links them by URL and caches nothing.
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(assetsDir),
    RequestPath = "/assets"
});

app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/api"))
        Console.WriteLine($"REQ {DateTime.Now:HH:mm:ss} {ctx.Request.Method} {ctx.Request.Path} origin={ctx.Request.Headers.Origin}");
    await next();
});

// The stage is a plain page. It talks to the same public endpoints any other client would,
// so a Unity stage and a browser stage can be open at once without stepping on each other.
app.MapGet("/", () => Results.Redirect("/stage"));
app.MapGet("/stage", () => File.Exists(stagePage)
    ? Results.File(stagePage, "text/html; charset=utf-8")
    : Results.NotFound(new { error = "舞台页面不在这个位置：" + stagePage }));

app.MapGet("/api/health", () => Results.Ok(new
{
    ok = true, service = "clarry", stage = world.Stage.Count, capacity = StageLimits.MaxActiveAgents,
    round = world.CurrentRound?.Number ?? 0, dice = world.Dice.Enabled,
    driven = loop.DrivenIds.Count, looping = loop.Running,
    loaded = snapshot is not null, file = store.Path
}));

app.MapGet("/api/world", () => Results.Ok(new
{
    world.Id, world.Title, world.Genre, world.Premise,
    registry = world.Registry.Count,
    stage = world.Stage.Active,
    capacity = world.Stage.Capacity,
    scenes = new
    {
        open = world.Scenes.Count(s => s.Open),
        closed = world.Scenes.Count(s => !s.Open),
        occupied = world.OccupiedCount,
        room = world.RoomLeft,
        active = world.Active.Id
    },
    rounds = world.Rounds.Count,
    turns = world.History.Count,
    clues = new { world.CluesResolved, world.ClueGoal },
    wishes = new { left = world.WishesLeft, budget = world.WishBudget, granted = world.Wishes.Select(w => w.Note) },
    forced = new { a = world.ForcedPairA, b = world.ForcedPairB },
    dice = new { world.Dice.Enabled, world.Dice.Notation }
}));

// Two different limits, on purpose. The sixty-four is how many residents may be in a scene at
// once -- beyond that a scene stops being readable. Staff are bounded by nothing except how
// many a venue needs to feel staffed, which is a completely different number.
app.MapGet("/api/tiers", () => Results.Ok(new
{
    capacity = StageLimits.MaxActiveAgents,
    seated = world.Stage.Count,
    principals = world.Registry.Values.Count(c => c.Tier == SeatTier.Principal),
    supporting = world.Registry.Values.Count(c => c.Tier == SeatTier.Supporting),
    residents = world.Registry.Values
        .Where(c => c.Tier == SeatTier.Principal)
        .Select(c => new { c.Id, c.Name, seated = world.Stage.Active.Contains(c.Id) }),
    staff = world.Registry.Values
        .Where(c => c.Tier == SeatTier.Supporting)
        .Select(c => new { c.Id, c.Name, c.Sheet.Role })
}));

// The public face of every seated character: enough for the stage to draw a card,
// and nothing that a Probe would have to be spent to learn.
app.MapGet("/api/cast", () => Results.Ok(
    world.Stage.Active
        .Select(id => world.Find(id))
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
            // The face is decided by the settlement, not by the viewer. This field only names
            // which file to try; the stage falls back to the neutral portrait when the file is
            // missing, which is what every character looks like until variants are drawn.
            expression = (world.Faces.GetValueOrDefault(c.Id, Expression.Neutral)).ToString().ToLowerInvariant(),
            expressionText = ExpressionRules.Text(world.Faces.GetValueOrDefault(c.Id, Expression.Neutral)),
            // Which image to show is a list, most specific first. The stage walks it and stops at
            // the first file that loads, so an undrawn close-up falls back to the pixel portrait
            // instead of leaving a hole where a character should be.
            shot = (world.Shots.GetValueOrDefault(c.Id, Shot.Wide)).ToString().ToLowerInvariant(),
            shotText = ShotRules.Text(world.Shots.GetValueOrDefault(c.Id, Shot.Wide)),
            faces = ShotRules.Candidates(
                c.Id,
                world.Faces.GetValueOrDefault(c.Id, Expression.Neutral),
                world.Shots.GetValueOrDefault(c.Id, Shot.Wide)),
            face = ShotRules.Candidates(
                c.Id,
                world.Faces.GetValueOrDefault(c.Id, Expression.Neutral),
                world.Shots.GetValueOrDefault(c.Id, Shot.Wide))[0],
            tier = c.Tier.ToString().ToLowerInvariant(),
            driven = loop.DrivenIds.Contains(c.Id),
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

// Who would share a scene, and why. Deterministic by design: the same world always proposes
// the same staging, so an audience coming back finds the people it left sitting together.
app.MapGet("/api/scene/propose", () => Results.Ok(
    ScenePicker.Propose(world).Select(p => new
    {
        cast = p.Cast,
        reason = p.Reason,
        solo = p.IsSolo
    })));

// The audience's wishes. Four kinds, three per scene, and no way to spend a character's
// attributes on them -- the cost is the wish itself. The audience may arrange an encounter,
// never decide what the characters say inside it.
app.MapPost("/api/wish", async (WishRequest r) =>
{
    await gate.WaitAsync();
    try
    {
        if (world.WishesLeft <= 0)
            return Results.Conflict(new { error = "许愿池干了，这一场没有愿望了" });

        Wish wish;
        switch (r.Kind)
        {
            case WishKind.BringTogether:
            {
                var a = r.SubjectId is null ? null : world.Find(r.SubjectId);
                var b = r.TargetId is null ? null : world.Find(r.TargetId);
                if (a is null || b is null) return Results.NotFound(new { error = "没有这个角色" });
                if (a.Id == b.Id) return Results.BadRequest(new { error = "得是两个人" });

                world.ForcedPairA = a.Id;
                world.ForcedPairB = b.Id;
                wish = Wish.Bring(a.Id, b.Id);
                break;
            }

            case WishKind.Kindle:
            {
                var c = r.SubjectId is null ? null : world.Find(r.SubjectId);
                if (c is null) return Results.NotFound(new { error = "没有这个角色" });
                c.Attributes.Ember = true;
                wish = Wish.Kindle(c.Id);
                break;
            }

            case WishKind.Forget:
            {
                var c = r.SubjectId is null ? null : world.Find(r.SubjectId);
                if (c is null) return Results.NotFound(new { error = "没有这个角色" });

                var coldest = c.Attributes.Bond
                    .OrderBy(kv => kv.Value)
                    .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                    .FirstOrDefault();
                if (coldest.Key is not null) c.Attributes.Bond.Remove(coldest.Key);
                wish = Wish.Forget(c.Id, coldest.Key);
                break;
            }

            case WishKind.Focus:
            {
                // The one wish that moves nothing but the camera. Watching a story is not only
                // about what happens; it is also about which of the things happening you see.
                if (string.IsNullOrWhiteSpace(r.SubjectId))
                    return Results.BadRequest(new { error = "得说清看哪一幕" });

                var target = world.Scenes.FirstOrDefault(s => s.Id == r.SubjectId && s.Open);
                if (target is null) return Results.NotFound(new { error = "没有开着这一幕：" + r.SubjectId });

                world.FocusScene = target.Id;
                wish = Wish.Focus(target.Id);
                break;
            }

            case WishKind.Halt:
            {
                loop.Stop("观众许愿让这一幕停下");
                wish = Wish.Halt();
                break;
            }

            default:
                return Results.BadRequest(new { error = "不认识的愿望" });
        }

        world.WishesLeft--;
        world.Wishes.Add(wish);
        Persist();

        return Results.Ok(new
        {
            granted = wish.Kind.ToString(),
            note = wish.Note,
            left = world.WishesLeft,
            budget = world.WishBudget
        });
    }
    finally { gate.Release(); }
});

// What A calls B. Not decoration: a nickname is the cheapest way two characters read as close,
// and it only works if it reaches the prompt -- a name the model never sees is a name it never
// says. Naming someone warms both sides a little, because naming is itself a move.
app.MapPost("/api/address", async (AddressRequest r) =>
{
    await gate.WaitAsync();
    try
    {
        var from = world.Find(r.ActorId);
        var to = world.Find(r.TargetId);
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
        world.Record(from.Id, "speak", $"从此管 {to.Id} 叫「{nickname}」", to.Id);
        Persist();

        return Results.Ok(new
        {
            from = from.Id,
            to = to.Id,
            nickname,
            bond = from.Attributes.BondWith(to.Id)
        });
    }
    finally { gate.Release(); }
});

// --- activities ------------------------------------------------------------
// Characters invent these themselves: a drinking contest, a wager, a race, a séance. The
// world records the rules, the moves and the verdict, then applies only the effects the
// verdict declares. It never runs the rules -- that is what keeps this open-ended instead
// of turning into an interpreter, and what makes a crooked judge a scene rather than a bug.

app.MapGet("/api/activity", () => Results.Ok(world.Activities.Select(a => new
{
    a.Id, a.Name, a.HostId, a.Rules,
    participants = a.Participants,
    moves = a.Moves,
    a.JudgedBy, a.Verdict, a.Settled,
    effects = a.Effects.Select(e => new { e.ActorId, e.Delta })
})));

app.MapPost("/api/activity/open", async (ActivityOpenRequest r) =>
{
    await gate.WaitAsync();
    try
    {
        if (string.IsNullOrWhiteSpace(r.Name) || string.IsNullOrWhiteSpace(r.Rules))
            return Results.BadRequest(new { error = "活动得有名字，也得有规则" });
        if (world.Find(r.HostId) is null)
            return Results.NotFound(new { error = "没有这个角色：" + r.HostId });

        var participants = (r.Participants ?? Array.Empty<string>())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (participants.Count < 2)
            return Results.BadRequest(new { error = "活动至少要两个人" });
        foreach (var id in participants)
            if (world.Find(id) is null) return Results.NotFound(new { error = "没有这个角色：" + id });

        var activity = new Activity
        {
            Id = "act-" + (world.Activities.Count + 1),
            Name = r.Name.Trim(),
            HostId = r.HostId,
            Rules = r.Rules.Trim()
        };
        activity.Participants.AddRange(participants);
        world.Activities.Add(activity);
        world.Record(r.HostId, "activity", $"提出了一场「{activity.Name}」：{activity.Rules}", null);
        Persist();

        return Results.Ok(new { opened = activity.Id, name = activity.Name, participants });
    }
    finally { gate.Release(); }
});

app.MapPost("/api/activity/move", async (ActivityMoveRequest r) =>
{
    await gate.WaitAsync();
    try
    {
        var activity = world.Activities.FirstOrDefault(a => a.Id == r.ActivityId);
        if (activity is null) return Results.NotFound(new { error = "没有这个活动：" + r.ActivityId });
        if (activity.Settled) return Results.Conflict(new { error = "这场活动已经结束了" });
        if (!activity.Participants.Contains(r.ActorId))
            return Results.BadRequest(new { error = r.ActorId + " 不在参与者里" });
        if (string.IsNullOrWhiteSpace(r.Move))
            return Results.BadRequest(new { error = "提交不能是空的" });

        activity.Moves[r.ActorId] = r.Move;
        Persist();

        return Results.Ok(new
        {
            submitted = r.ActorId,
            moves = activity.Moves.Count,
            participants = activity.Participants.Count,
            everyoneMoved = activity.EveryoneMoved()
        });
    }
    finally { gate.Release(); }
});

app.MapPost("/api/activity/close", async (ActivityCloseRequest r) =>
{
    await gate.WaitAsync();
    try
    {
        var activity = world.Activities.FirstOrDefault(a => a.Id == r.ActivityId);
        if (activity is null) return Results.NotFound(new { error = "没有这个活动：" + r.ActivityId });
        if (activity.Settled) return Results.Conflict(new { error = "这场活动已经结束了" });
        if (string.IsNullOrWhiteSpace(r.Verdict))
            return Results.BadRequest(new { error = "裁决不能是空的" });
        if (world.Find(r.JudgedBy) is null)
            return Results.NotFound(new { error = "没有这个角色：" + r.JudgedBy });

        activity.JudgedBy = r.JudgedBy;
        activity.Verdict = r.Verdict.Trim();
        activity.Settled = true;
        activity.SettledAt = DateTimeOffset.UtcNow;

        foreach (var e in r.Effects ?? Array.Empty<ActivityEffectRequest>())
        {
            var c = world.Find(e.ActorId);
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

        world.Record(activity.HostId, "activity", $"「{activity.Name}」的结果：{activity.Verdict}", null);
        Persist();

        return Results.Ok(new
        {
            settled = activity.Id,
            judgedBy = activity.JudgedBy,
            verdict = activity.Verdict,
            effects = activity.Effects.Select(x => new { x.ActorId, x.Delta })
        });
    }
    finally { gate.Release(); }
});

// What one character still remembers, heaviest first. Two characters at the same event do
// not come away with the same amount of it, so this is per-person by design.
app.MapGet("/api/memory/{id}", (string id) =>
{
    var c = world.Find(id);
    if (c is null) return Results.NotFound(new { error = "没有这个角色：" + id });

    var style = MemoryStyle.For(c);
    var kept = memories.Recall(id, 50);
    return Results.Ok(new
    {
        agent = id,
        style = new { decay = style.Decay, salience = style.Salience, capacity = style.Capacity, recall = style.Recall },
        total = memories.Count(id),
        kept = kept.Select(m => new { m.Text, m.AboutId, weight = Math.Round(m.Weight, 1), at = m.At })
    });
});

// Forgetting on purpose: one character's memory, or all of it. A new scene keeps memories by
// default -- they are the one thing that is supposed to outlive a scene.
app.MapPost("/api/memory/clear", (string? agentId) =>
{
    var removed = memories.Clear(agentId);
    return Results.Ok(new { cleared = agentId ?? "*", rows = removed });
});

// --- the audience's camera ----------------------------------------------------------------
// Which scene is on screen. The default is "whatever moved last", because an audience should not
// have to keep choosing; pinning exists for when they do want to keep watching one room while
// something louder happens elsewhere.

app.MapGet("/api/audience", () =>
{
    var focused = world.Focus();

    return Results.Ok(new
    {
        watching = focused?.Id,
        title = focused?.Title,
        because = world.FocusBecause(),
        // Only a pin that still points at an open scene is a pin. One whose scene has closed is
        // history, and reporting it would suggest the audience is watching something it is not.
        pinned = world.FocusScene is not null
                 && world.Scenes.Any(s => s.Id == world.FocusScene && s.Open)
            ? world.FocusScene
            : null,
        open = world.Scenes.Where(s => s.Open).Select(s => new
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
        wishes = new { left = world.WishesLeft, budget = world.WishBudget }
    });
});

app.MapPost("/api/audience/follow", async (FollowRequest r) =>
{
    var target = world.Scenes.FirstOrDefault(s => s.Id == r.SceneId && s.Open);
    if (target is null) return Results.NotFound(new { error = "没有开着这一幕：" + r.SceneId });

    await gate.WaitAsync();
    try
    {
        world.FocusScene = target.Id;
        Persist();
    }
    finally { gate.Release(); }

    return Results.Ok(new { watching = target.Id, because = world.FocusBecause() });
});

app.MapPost("/api/audience/auto", async () =>
{
    await gate.WaitAsync();
    try
    {
        world.FocusScene = null;
        Persist();
    }
    finally { gate.Release(); }

    var focused = world.Focus();
    return Results.Ok(new { watching = focused?.Id, because = world.FocusBecause() });
});

// A line said to one named scene, not to whoever happens to be active. Narration has no actor
// and no turn: it is the world remarking on a room, and it moves that room's camera because a
// line of history is exactly what the audience is watching for.
app.MapPost("/api/scene/narrate", async (NarrateRequest r) =>
{
    var target = world.Scenes.FirstOrDefault(s => s.Id == r.SceneId);
    if (target is null) return Results.NotFound(new { error = "没有这一幕：" + r.SceneId });
    if (string.IsNullOrWhiteSpace(r.Text)) return Results.BadRequest(new { error = "总要有点话说" });

    await gate.WaitAsync();
    try
    {
        target.Record(r.ActorId ?? "世界", "narration", r.Text, null);
        Persist();
    }
    finally { gate.Release(); }

    return Results.Ok(new
    {
        scene = target.Id,
        lines = target.History.Count,
        touchedAt = target.LastChangedAt,
        watching = world.Focus()?.Id
    });
});

// --- scenes -------------------------------------------------------------------------------
// A world holds several stretches of play at once. Each owns its cast, rounds and history; the
// people and their relationships outlive all of them. Sixty-four is the whole world's cap on
// residents busy at once, not one scene's.

object DescribeScene(Scene s) => new
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

app.MapGet("/api/scenes", () => Results.Ok(new
{
    open = world.Scenes.Where(s => s.Open).Select(DescribeScene),
    closed = world.Scenes.Where(s => !s.Open).Select(DescribeScene),
    occupied = world.OccupiedCount,
    capacity = StageLimits.MaxActiveAgents,
    room = world.RoomLeft
}));

app.MapPost("/api/scenes/open", async (SceneOpenRequest r) =>
{
    await gate.WaitAsync();
    try
    {
        var (scene, reason) = world.OpenScene(r.Cast ?? Array.Empty<string>(), r.Title ?? "");
        if (scene is null) return Results.Conflict(new { error = reason });
        Persist();
        return Results.Ok(new
        {
            opened = scene.Id,
            number = scene.Number,
            cast = scene.Cast.Active,
            occupied = world.OccupiedCount
        });
    }
    finally { gate.Release(); }
});

app.MapPost("/api/scenes/close", async (string? sceneId) =>
{
    await gate.WaitAsync();
    try
    {
        var id = string.IsNullOrWhiteSpace(sceneId) ? world.Active.Id : sceneId;
        var closed = world.CloseScene(id);
        if (closed) Persist();
        return Results.Ok(new { closed = id, wasOpen = closed, occupied = world.OccupiedCount });
    }
    finally { gate.Release(); }
});

// Closes every open scene, then casts afresh from the pairing rule. This is the step that turns
// ScenePicker's proposals into actual parallel scenes instead of a report nobody acts on.
app.MapPost("/api/scenes/auto", async () =>
{
    await gate.WaitAsync();
    try
    {
        foreach (var s in world.Scenes.Where(s => s.Open).ToList()) world.CloseScene(s.Id);

        // Only residents are cast; staff belong to a venue, not to a scene.
        var free = world.Registry.Values
            .Where(c => c.Tier == SeatTier.Principal)
            .Select(c => c.Id)
            .ToList();

        var opened = new List<object>();
        var refused = new List<object>();

        foreach (var proposal in ScenePicker.Propose(world, free))
        {
            var (scene, reason) = world.OpenScene(proposal.Cast, proposal.Reason);
            if (scene is null) refused.Add(new { cast = proposal.Cast, reason });
            else opened.Add(new { scene.Id, scene.Number, cast = scene.Cast.Active, reason = proposal.Reason });
        }

        Persist();
        return Results.Ok(new
        {
            opened,
            refused,
            occupied = world.OccupiedCount,
            capacity = StageLimits.MaxActiveAgents
        });
    }
    finally { gate.Release(); }
});

// A new scene clears the history, the round ledger and the clue progress. The cast stays:
// characters keep whatever state the last play left them in.
app.MapPost("/api/scene/new", async (int? clueGoal) =>
{
    if (loop.Running) return Results.Conflict(new { error = "循环正在跑，先收场再开新戏" });
    await gate.WaitAsync();
    try
    {
        world.ResetScene(clueGoal);
        Persist();
        return Results.Ok(new
        {
            opened = true, clues = world.ClueGoal, stage = world.Stage.Active,
            turns = world.History.Count, rounds = world.Rounds.Count
        });
    }
    finally { gate.Release(); }
});

app.MapPost("/api/character", async (Character c) =>
{
    if (string.IsNullOrWhiteSpace(c.Id) || string.IsNullOrWhiteSpace(c.Name))
        return Results.BadRequest(new { error = "id 与 name 都不能为空" });
    await gate.WaitAsync();
    try { world.Add(c); Persist(); }
    finally { gate.Release(); }
    return Results.Ok(new { added = c.Id, registry = world.Registry.Count });
});

// One character, in full. The prompt is built from exactly these fields, so this is also the
// honest way to check that something a model is supposed to see actually reached it.
app.MapGet("/api/character/{id}", (string id) =>
{
    var c = world.Find(id);
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
    var candidate = world.Find(r.Id);
    if (candidate is null) return Results.NotFound(new { error = "没有这个角色：" + r.Id });
    if (candidate.Tier == SeatTier.Supporting)
        return Results.BadRequest(new { error = "店员不占台位，六十四席只留给主角色" });

    Stage.EnterResult result;
    await gate.WaitAsync();
    try
    {
        result = world.Stage.Enter(r.Id);
        if (result.Entered) Persist();
    }
    finally { gate.Release(); }

    return result.Entered
        ? Results.Ok(new { entered = r.Id, stage = world.Stage.Active, count = world.Stage.Count, capacity = world.Stage.Capacity })
        : Results.Conflict(new { error = result.Reason, count = world.Stage.Count, capacity = world.Stage.Capacity });
});

app.MapPost("/api/stage/leave", async (EnterRequest r) =>
{
    bool left;
    await gate.WaitAsync();
    try { left = world.Stage.Leave(r.Id); if (left) Persist(); }
    finally { gate.Release(); }
    return Results.Ok(new { left = r.Id, removed = left, count = world.Stage.Count });
});

app.MapPost("/api/round/begin", async () =>
{
    await gate.WaitAsync();
    try
    {
        var r = world.BeginRound();
        Persist();
        return Results.Ok(new { round = r.Number, open = !r.Closed });
    }
    catch (InvalidOperationException e) { return Results.Conflict(new { error = e.Message }); }
    finally { gate.Release(); }
});

app.MapGet("/api/round/current", () =>
{
    var r = world.CurrentRound;
    if (r is null) return Results.Ok(new { round = 0, open = false, orders = Array.Empty<object>() });
    return Results.Ok(new
    {
        round = r.Number, open = !r.Closed,
        orders = r.Orders.Select(o => new
        {
            o.ActorId,
            kind = o.Kind.ToString().ToLowerInvariant(),
            kindLabel = OrderKindText.Of(o.Kind),
            o.TargetId, o.Text, o.Cost
        })
    });
});

app.MapPost("/api/round/order", async (OrderRequest r) =>
{
    await gate.WaitAsync();
    try
    {
        var round = world.CurrentRound;
        if (round is null) return Results.Conflict(new { error = "还没有开始的回合，先开一轮" });
        if (round.Closed) return Results.Conflict(new { error = "这一轮已经关闭" });
        if (world.Find(r.ActorId) is null) return Results.NotFound(new { error = "没有这个角色：" + r.ActorId });
        round.Add(new Order { ActorId = r.ActorId, Kind = r.Kind, TargetId = r.TargetId, Text = r.Text, Sequence = round.Orders.Count });
        return Results.Ok(new { accepted = true, round = round.Number, orders = round.Orders.Count });
    }
    finally { gate.Release(); }
});

app.MapPost("/api/round/resolve", async () =>
{
    if (loop.Running) return Results.Conflict(new { error = "循环正在驱动这个世界，先收场" });
    await gate.WaitAsync();
    try
    {
        var round = world.CurrentRound;
        if (round is null) return Results.Conflict(new { error = "还没有回合" });
        if (round.Result is not null) return Results.Conflict(new { error = "这一轮已经结算过了" });
        round.Close();
        var result = RoundResolver.Resolve(round, world);
        MemoryRecorder.Record(round, world, memories);
        world.TurnPointer = world.Stage.NextTurn(world.TurnPointer);
        Persist();
        return Results.Ok(new
        {
            round = result.Round, converged = result.Converged, revealed = result.Revealed,
            ignored = result.Ignored.Select(o => new { o.ActorId, kind = o.Kind.ToString().ToLowerInvariant() }),
            notes = result.Notes, deltas = result.Deltas,
            stage = world.Stage.Active, next = world.CurrentActor()?.Id
        });
    }
    finally { gate.Release(); }
});

// --- drivers ---------------------------------------------------------------
// A driver is what turns a character into orders. Attaching one is how an outside
// agent -- a hosted model, a local runner, anything with an HTTP face -- takes a seat.

app.MapPost("/api/driver/attach", (AttachRequest r) =>
{
    var c = world.Find(r.Id);
    if (c is null) return Results.NotFound(new { error = "没有这个角色：" + r.Id });

    IAgentDriver driver;
    if (string.Equals(r.Mode, "scripted", StringComparison.OrdinalIgnoreCase))
    {
        driver = ScriptedDriver.Alternating();
    }
    else
    {
        if (string.IsNullOrWhiteSpace(r.Endpoint))
            return Results.BadRequest(new { error = "除了 mode=scripted，都必须给 endpoint" });
        driver = new HttpLlmDriver(new HttpDriverOptions
        {
            Endpoint = r.Endpoint,
            Model = r.Model,
            ApiKey = r.ApiKey,
            Temperature = r.Temperature ?? 0.9,
            MaxTokens = r.MaxTokens ?? 800,
            TimeoutMs = r.TimeoutMs ?? 180_000
        });
    }

    loop.Attach(r.Id, driver);
    return Results.Ok(new { attached = r.Id, kind = driver.Kind, driven = loop.DrivenIds });
});

app.MapPost("/api/driver/detach", (EnterRequest r) =>
{
    var removed = loop.Detach(r.Id);
    return Results.Ok(new { detached = r.Id, removed, driven = loop.DrivenIds });
});

app.MapGet("/api/driver/list", () => Results.Ok(new { driven = loop.DrivenIds }));

// --- the loop --------------------------------------------------------------

app.MapPost("/api/loop/start", (LoopStartRequest? r) =>
{
    loop.Start(r?.IntervalMs ?? 0, r?.MaxRounds ?? 0, r?.UserWaitMs ?? 0);
    return Results.Ok(loop.Snapshot());
});

app.MapPost("/api/loop/stop", (string? reason) =>
{
    loop.Stop(reason ?? "被要求收场");
    return Results.Ok(loop.Snapshot());
});

app.MapGet("/api/loop/status", () => Results.Ok(loop.Snapshot()));

// --- mini games ------------------------------------------------------------

app.MapPost("/api/minigame/civ-like", async () =>
{
    await gate.WaitAsync();
    try
    {
        var players = world.Stage.Active.Select(id => world.Find(id)).Where(c => c is not null).Select(c => c!).ToList();
        var game = new CivLikeGame();
        if (!game.CanStart(world, players))
            return Results.Conflict(new { error = "这个小游戏需要 2 到 4 位在台上的角色" });
        var result = game.Play(world, players);
        foreach (var (id, delta) in result.Effects)
        {
            var c = world.Find(id);
            if (c is null) continue;
            c.Attributes.Renown = Attributes.Clamp(c.Attributes.Renown + delta.Renown);
            c.Attributes.Strain = Attributes.Clamp(c.Attributes.Strain + delta.Strain);
            c.Attributes.Reserve = Attributes.Clamp(c.Attributes.Reserve + delta.Reserve);
            world.Record(id, "game", "围着一张小棋盘下了一局，胜者是 " + result.WinnerId, null);
        }
        Persist();
        return Results.Ok(new { game = result.GameId, winner = result.WinnerId, usedDice = result.UsedDice, effects = result.Effects, log = result.Log });
    }
    finally { gate.Release(); }
});

// A two-player card duel. Pass ?a=&b= to pick the seats, or let it take the first two on
// stage. The shuffle is seeded from the world, so the same pairing deals the same hand.
app.MapPost("/api/minigame/card-duel", async (string? a, string? b) =>
{
    await gate.WaitAsync();
    try
    {
        List<Character> players;
        if (a is not null && b is not null)
        {
            var ca = world.Find(a);
            var cb = world.Find(b);
            if (ca is null || cb is null) return Results.NotFound(new { error = "没有这个角色" });
            if (ca.Id == cb.Id) return Results.BadRequest(new { error = "得是两个人" });
            players = new List<Character> { ca, cb };
        }
        else
        {
            players = world.Stage.Active
                .Select(id => world.Find(id))
                .Where(c => c is not null)
                .Select(c => c!)
                .Take(2)
                .ToList();
        }

        var game = new CardDuelGame();
        if (!game.CanStart(world, players))
            return Results.Conflict(new { error = "黑白对局需要正好两个人" });

        var result = game.Play(world, players);
        foreach (var (id, delta) in result.Effects)
        {
            var c = world.Find(id);
            if (c is null) continue;
            c.Attributes.Insight = Attributes.Clamp(c.Attributes.Insight + delta.Insight);
            c.Attributes.Renown = Attributes.Clamp(c.Attributes.Renown + delta.Renown);
            c.Attributes.Strain = Attributes.Clamp(c.Attributes.Strain + delta.Strain);
            c.Attributes.Reserve = Attributes.Clamp(c.Attributes.Reserve + delta.Reserve);
            world.Record(id, "game", $"打了一局黑白对局，{result.WinnerId} 胜", null);
        }
        Persist();

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
    finally { gate.Release(); }
});

// The co-operative interlude: keep a watch until morning. There is no winner -- the result
// is a shared held-or-not, and the effects land on everyone the same way.
app.MapPost("/api/minigame/vigil", async () =>
{
    await gate.WaitAsync();
    try
    {
        var players = world.Stage.Active
            .Select(id => world.Find(id))
            .Where(c => c is not null)
            .Select(c => c!)
            .Take(4)
            .ToList();

        var game = new VigilGame();
        if (!game.CanStart(world, players))
            return Results.Conflict(new { error = "守夜需要 2 到 4 个人在台上" });

        var result = game.Play(world, players);
        foreach (var (id, delta) in result.Effects)
        {
            var c = world.Find(id);
            if (c is null) continue;
            c.Attributes.Poise = Attributes.Clamp(c.Attributes.Poise + delta.Poise);
            c.Attributes.Renown = Attributes.Clamp(c.Attributes.Renown + delta.Renown);
            c.Attributes.Strain = Attributes.Clamp(c.Attributes.Strain + delta.Strain);
            c.Attributes.Reserve = Attributes.Clamp(c.Attributes.Reserve + delta.Reserve);
            world.Record(id, "game",
                result.TeamSucceeded == true ? "一起守到了天亮" : "守夜没撑住", null);
        }
        Persist();

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
    finally { gate.Release(); }
});

// --- ground, buildings, what is carried, and divination -------------------------------
// Three levels, and they are levels rather than labels. A plot is ground: a back garden is one
// of them, and it is not a building. A basic building stands on a plot. An extension has no
// ground of its own and is added to a basic building -- which is why an observatory cannot be
// bought on an empty lot, and why building takes a plot or a building but never both.

app.MapPost("/api/carry", async (CarryRequest r) =>
{
    var c = world.Find(r.Id);
    if (c is null) return Results.NotFound(new { error = "没有这个角色：" + r.Id });
    if (!ApparatusText.TryParse(r.Kind, out var kind))
        return Results.BadRequest(new { error = "没有这种器材：" + r.Kind });

    Carried? kit;
    string? reason;
    await gate.WaitAsync();
    try
    {
        (kit, reason) = Astrologer.Carry(c, kind, r.From);
        if (kit is not null) Persist();
    }
    finally { gate.Release(); }

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

// More ground inside a building that already exists: a cellar, a roof, whatever comes next. A
// basic building brings one plot with it (a workshop brings a backyard), so this is for adding
// to that, not for conjuring the first one.
app.MapPost("/api/plot", async (PlotRequest r) =>
{
    var c = world.Find(r.Id);
    if (c is null) return Results.NotFound(new { error = "没有这个角色：" + r.Id });
    if (!PlotText.TryParse(r.Kind, out var kind))
        return Results.BadRequest(new { error = "没有这种场地：" + r.Kind });

    Plot? plot;
    string? reason;
    await gate.WaitAsync();
    try
    {
        (plot, reason) = Astrologer.AddPlot(c, r.BuildingId, kind, r.Name);
        if (plot is not null) Persist();
    }
    finally { gate.Release(); }

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
    var c = world.Find(r.Id);
    if (c is null) return Results.NotFound(new { error = "没有这个角色：" + r.Id });
    if (!ApparatusText.TryParse(r.Kind, out var kind))
        return Results.BadRequest(new { error = "没有这种建筑：" + r.Kind });

    Building? built;
    string? reason;
    await gate.WaitAsync();
    try
    {
        (built, reason) = Astrologer.Build(c, kind, r.OnPlot);
        if (built is not null) Persist();
    }
    finally { gate.Release(); }

    if (built is null) return Results.Conflict(new { error = reason });

    return Results.Ok(new
    {
        built = built.Id,
        kind = built.Kind.ToString().ToLowerInvariant(),
        label = ApparatusText.Of(built.Kind),
        housing = built.Housing.ToString().ToLowerInvariant(),
        housingLabel = ApparatusRules.HousingText(built.Housing),
        onPlot = built.OnPlot,
        // A basic building answers with the ground it came with; an extension has none of its own.
        plots = built.Plots.Select(p => new { p.Id, label = p.Label }),
        standing = Astrologer.Standing(c, built),
        astrologer = Astrologer.IsAstrologer(c),
        missing = Astrologer.Missing(c).Select(ApparatusText.Of)
    });
});

// Everything one character holds, level by level. Reading it this way makes an inconsistency
// visible instead of silent: a building whose plot is gone shows up as standing=false rather
// than quietly disappearing from the list.
app.MapGet("/api/holdings/{id}", (string id) =>
{
    var c = world.Find(id);
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

// The sky is shared. One weather, one season, one night -- above every scene at once, which is
// exactly why hearing about it from someone else is worth something.
app.MapGet("/api/sky", () => Results.Ok(new
{
    weather = world.Sky.Weather,
    season = world.Sky.Season,
    night = world.Sky.Night,
    forecastBy = world.Sky.ForecastBy,
    at = world.Sky.At,
    text = world.Sky.Describe()
}));

app.MapPost("/api/sky", async (SkyRequest r) =>
{
    await gate.WaitAsync();
    try
    {
        if (!string.IsNullOrWhiteSpace(r.Weather)) world.Sky.Weather = r.Weather!;
        if (!string.IsNullOrWhiteSpace(r.Season)) world.Sky.Season = r.Season!;
        if (r.Night is { } n) world.Sky.Night = Math.Clamp(n, 0, 100);
        world.Sky.At = DateTimeOffset.UtcNow;
        Persist();
    }
    finally { gate.Release(); }
    return Results.Ok(new { weather = world.Sky.Weather, season = world.Sky.Season, text = world.Sky.Describe() });
});

// A reading by hand, for the audience and for tests: same code path the resolver uses during a
// round, so there is only ever one definition of what a reading does.
app.MapPost("/api/divine", async (DivineRequest r) =>
{
    var reader = world.Find(r.ById);
    if (reader is null) return Results.NotFound(new { error = "没有这个角色：" + r.ById });
    if (!ApparatusText.TryParse(r.Kind, out var tool))
        return Results.BadRequest(new { error = "没有这种器材：" + r.Kind });

    DivinationOutcome outcome;
    await gate.WaitAsync();
    try
    {
        var querent = r.TargetId is null ? null : world.Find(r.TargetId);
        var board = new RoundResult { Round = world.CurrentRound?.Number ?? 0 };
        outcome = Divination.Perform(world, board, reader, tool, querent, r.Question, r.Advice);
        if (outcome.Ok)
        {
            foreach (var line in board.Revealed) world.Record(reader.Id, "narration", line, r.TargetId);
            foreach (var note in board.Notes) world.Record(reader.Id, "narration", note, null);
            Persist();
        }
    }
    finally { gate.Release(); }

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

// The board game: buy property, pay rent, mortgage what cannot be covered, and go broke in
// public. Seeded, so the same table replays the same match -- and no dice escape the interlude.
app.MapPost("/api/minigame/board", async () =>
{
    await gate.WaitAsync();
    try
    {
        var players = world.Stage.Active
            .Select(id => world.Find(id))
            .Where(c => c is not null)
            .Select(c => c!)
            .Take(4)
            .ToList();

        var game = new BoardGame();
        if (!game.CanStart(world, players))
            return Results.Conflict(new { error = "大富翁需要 2 到 4 个人在台上" });

        var result = game.Play(world, players);
        foreach (var (id, delta) in result.Effects)
        {
            var c = world.Find(id);
            if (c is null) continue;
            c.Attributes.Renown = Attributes.Clamp(c.Attributes.Renown + delta.Renown);
            c.Attributes.Strain = Attributes.Clamp(c.Attributes.Strain + delta.Strain);
            c.Attributes.Reserve = Attributes.Clamp(c.Attributes.Reserve + delta.Reserve);
            world.Record(id, "game", $"打了一局大富翁，{result.WinnerId} 胜", null);
        }
        Persist();

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
    finally { gate.Release(); }
});

app.MapGet("/api/astrologers", () => Results.Ok(new
{
    astrologers = world.Registry.Values
        .Where(Astrologer.IsAstrologer)
        .Select(c => new { c.Id, proficiency = Astrologer.Proficiency(c), price = Astrologer.Price(c) }),
    sky = new { world.Sky.Weather, world.Sky.Season, world.Sky.Night, world.Sky.ForecastBy }
}));

// --- venues: a place someone runs -----------------------------------------------------
// A shop is not a scene. It stays where it is while the camera moves, it keeps its own books,
// and anyone may walk in. Everything here either happens or says why it did not; a half-served
// order would be a data bug wearing a friendly face.

object DescribeVenue(Venue v) => new
{
    v.Id,
    v.Name,
    v.OwnerId,
    kind = v.Kind.ToString().ToLowerInvariant(),
    kindLabel = VenueText.Of(v.Kind),
    v.Open,
    plots = v.Plots.Select(p => new
    {
        p.Id,
        kind = p.Kind.ToString().ToLowerInvariant(),
        label = p.Label
    }),
    menu = v.Menu.Select(m => new
    {
        m.Name,
        m.Price,
        m.Effort,
        needs = m.Needs.Select(n => new { n.Item, n.Count })
    }),
    stock = v.Stock.Select(s => new { s.Name, s.Count }),
    staff = v.Staff.Select(s => new { s.CharacterId, s.Role, s.Wage, s.OnDuty }),
    balance = v.Balance,
    v.Reputation,
    v.DaysOpen,
    capacity = Shifts.Capacity(v),
    shift = v.Shift is null ? null : new
    {
        open = v.Shift.Open,
        inLine = v.Shift.Queue.Count,
        served = v.Shift.Served.Count,
        walkedOut = v.Shift.WalkedOut.Count
    },
    ledger = v.Ledger.TakeLast(20).Select(l => new
    {
        kind = l.Kind.ToString().ToLowerInvariant(),
        l.Amount,
        l.Note,
        l.WhoId,
        l.At
    })
};

app.MapGet("/api/venues", () => Results.Ok(new
{
    venues = world.Venues.Select(v => new
    {
        v.Id,
        v.Name,
        v.OwnerId,
        kind = v.Kind.ToString().ToLowerInvariant(),
        v.Open,
        balance = v.Balance,
        dishes = v.Menu.Count,
        staff = v.OnDutyCount
    })
}));

app.MapGet("/api/venue/{id}", (string id) =>
{
    var v = world.Venues.FirstOrDefault(x => x.Id == id);
    if (v is null) return Results.NotFound(new { error = "没有这家店：" + id });
    return Results.Ok(DescribeVenue(v));
});

app.MapPost("/api/venue", async (VenueOpenRequest r) =>
{
    var owner = world.Find(r.OwnerId);
    if (owner is null) return Results.NotFound(new { error = "没有这个角色：" + r.OwnerId });
    if (!VenueText.TryParse(r.Kind ?? "cafe", out var kind))
        return Results.BadRequest(new { error = "没有这种店：" + r.Kind });

    Venue? venue;
    string? reason;
    await gate.WaitAsync();
    try
    {
        (venue, reason) = Trade.Open(world, owner, r.Name, kind);
        if (venue is not null) Persist();
    }
    finally { gate.Release(); }

    if (venue is null) return Results.Conflict(new { error = reason });
    return Results.Ok(DescribeVenue(venue));
});

app.MapPost("/api/venue/toggle", async (VenueToggleRequest r) =>
{
    var v = world.Venues.FirstOrDefault(x => x.Id == r.VenueId);
    if (v is null) return Results.NotFound(new { error = "没有这家店：" + r.VenueId });

    await gate.WaitAsync();
    try { v.Open = r.Open; Persist(); }
    finally { gate.Release(); }

    return Results.Ok(new { venue = v.Id, open = v.Open });
});

app.MapPost("/api/venue/menu", async (VenueMenuRequest r) =>
{
    var v = world.Venues.FirstOrDefault(x => x.Id == r.VenueId);
    if (v is null) return Results.NotFound(new { error = "没有这家店：" + r.VenueId });

    MenuItem? dish;
    string? reason;
    await gate.WaitAsync();
    try
    {
        var needs = (r.Needs ?? Array.Empty<NeedRequest>())
            .Where(n => !string.IsNullOrWhiteSpace(n.Item))
            .Select(n => new Need { Item = n.Item, Count = Math.Max(1, n.Count) });
        (dish, reason) = Trade.Offer(v, r.Name, r.Price, r.Effort ?? 1, needs);
        if (dish is not null) Persist();
    }
    finally { gate.Release(); }

    if (dish is null) return Results.Conflict(new { error = reason });
    return Results.Ok(new
    {
        dish = dish.Name,
        dish.Price,
        dish.Effort,
        needs = dish.Needs.Select(n => new { n.Item, n.Count })
    });
});

app.MapPost("/api/venue/stock", async (VenueStockRequest r) =>
{
    var v = world.Venues.FirstOrDefault(x => x.Id == r.VenueId);
    if (v is null) return Results.NotFound(new { error = "没有这家店：" + r.VenueId });

    var payer = r.PayerId is null ? null : world.Find(r.PayerId);
    if (r.PayerId is not null && payer is null)
        return Results.NotFound(new { error = "没有这个角色：" + r.PayerId });

    StockItem? item;
    string? reason;
    await gate.WaitAsync();
    try
    {
        (item, reason) = Trade.Supply(v, payer, r.Item, r.Count, r.UnitCost ?? 0);
        if (item is not null) Persist();
    }
    finally { gate.Release(); }

    if (item is null) return Results.Conflict(new { error = reason });
    return Results.Ok(new { venue = v.Id, item = item.Name, item.Count, balance = v.Balance });
});

app.MapPost("/api/venue/staff", async (VenueStaffRequest r) =>
{
    var v = world.Venues.FirstOrDefault(x => x.Id == r.VenueId);
    if (v is null) return Results.NotFound(new { error = "没有这家店：" + r.VenueId });

    Staff? job;
    string? reason;
    await gate.WaitAsync();
    try
    {
        (job, reason) = Trade.Hire(v, world, r.StaffId, r.Wage ?? 0, r.Role ?? "店员");
        if (job is not null) Persist();
    }
    finally { gate.Release(); }

    if (job is null) return Results.Conflict(new { error = reason });
    return Results.Ok(new
    {
        venue = v.Id,
        staff = job.CharacterId,
        job.Role,
        job.Wage,
        job.OnDuty
    });
});

app.MapPost("/api/venue/order", async (VenueOrderRequest r) =>
{
    var v = world.Venues.FirstOrDefault(x => x.Id == r.VenueId);
    if (v is null) return Results.NotFound(new { error = "没有这家店：" + r.VenueId });

    var guest = world.Find(r.GuestId);
    if (guest is null) return Results.NotFound(new { error = "没有这个角色：" + r.GuestId });

    var server = r.ServerId is null ? null : world.Find(r.ServerId);
    if (r.ServerId is not null && server is null)
        return Results.NotFound(new { error = "没有这个角色：" + r.ServerId });

    Receipt? receipt;
    string? reason;
    await gate.WaitAsync();
    try
    {
        (receipt, reason) = Trade.Serve(world, v, guest, r.Dish, server);
        if (receipt is not null)
        {
            world.Record(r.GuestId, "act", $"在{v.Name}要了一份{receipt.Dish}", server?.Id);
            Persist();
        }
    }
    finally { gate.Release(); }

    if (receipt is null) return Results.Conflict(new { error = reason });
    return Results.Ok(new
    {
        venue = receipt.VenueId,
        dish = receipt.Dish,
        receipt.Price,
        guest = receipt.GuestId,
        servedBy = receipt.ServedById,
        effects = receipt.Effects,
        balance = v.Balance
    });
});

app.MapPost("/api/venue/wages", async (VenueWagesRequest r) =>
{
    var v = world.Venues.FirstOrDefault(x => x.Id == r.VenueId);
    if (v is null) return Results.NotFound(new { error = "没有这家店：" + r.VenueId });

    int paid;
    int shortfall;
    string? reason;
    await gate.WaitAsync();
    try
    {
        (paid, shortfall, reason) = Trade.PayWages(world, v);
        Persist();
    }
    finally { gate.Release(); }

    return Results.Ok(new { venue = v.Id, paid = paid, shortfall = shortfall, note = reason, balance = v.Balance });
});

// --- the shift: one evening of business ------------------------------------------------
// A queue, not a round. People arrive one at a time and are served one at a time, so there is
// nothing to synchronise and no round machinery anywhere in here. Every step is an event.

object DescribeShift(Shift s) => new
{
    s.VenueId,
    s.Open,
    queue = s.Queue.Select(g => new { g.CharacterId, g.Want, g.ArrivedAt }),
    s.Served,
    s.WalkedOut,
    s.StartedAt,
    s.EndedAt
};

app.MapGet("/api/venue/shift/{id}", (string id) =>
{
    var v = world.Venues.FirstOrDefault(x => x.Id == id);
    if (v is null) return Results.NotFound(new { error = "没有这家店：" + id });
    if (v.Shift is null) return Results.Ok(new { venue = id, open = false, queue = Array.Empty<object>() });
    return Results.Ok(DescribeShift(v.Shift));
});

app.MapPost("/api/venue/shift/open", async (ShiftVenueRequest r) =>
{
    var v = world.Venues.FirstOrDefault(x => x.Id == r.VenueId);
    if (v is null) return Results.NotFound(new { error = "没有这家店：" + r.VenueId });

    Shift? shift;
    string? reason;
    await gate.WaitAsync();
    try
    {
        (shift, reason) = Shifts.Open(v);
        if (shift is not null) Persist();
    }
    finally { gate.Release(); }

    if (shift is null) return Results.Conflict(new { error = reason });
    return Results.Ok(new { venue = v.Id, open = shift.Open, capacity = Shifts.Capacity(v), reputation = v.Reputation });
});

app.MapPost("/api/venue/shift/arrive", async (ShiftArriveRequest r) =>
{
    var v = world.Venues.FirstOrDefault(x => x.Id == r.VenueId);
    if (v is null) return Results.NotFound(new { error = "没有这家店：" + r.VenueId });

    WaitingGuest? guest;
    string? reason;
    int capacity;
    await gate.WaitAsync();
    try
    {
        (guest, reason, capacity) = Shifts.Arrive(world, v, r.GuestId, r.Want);
        if (guest is not null) Persist();
    }
    finally { gate.Release(); }

    if (guest is null) return Results.Conflict(new { error = reason, capacity, reputation = v.Reputation });
    return Results.Ok(new
    {
        arrived = guest.CharacterId,
        want = guest.Want,
        inLine = v.Shift?.Queue.Count ?? 0,
        capacity,
        reputation = v.Reputation
    });
});

app.MapPost("/api/venue/shift/serve", async (ShiftServeRequest r) =>
{
    var v = world.Venues.FirstOrDefault(x => x.Id == r.VenueId);
    if (v is null) return Results.NotFound(new { error = "没有这家店：" + r.VenueId });

    var server = r.ServerId is null ? null : world.Find(r.ServerId);
    if (r.ServerId is not null && server is null)
        return Results.NotFound(new { error = "没有这个角色：" + r.ServerId });

    Receipt? receipt;
    string? reason;
    int reputation;
    await gate.WaitAsync();
    try
    {
        (receipt, reason, reputation) = Shifts.ServeHead(world, v, server, r.Dish);
        if (receipt is not null)
        {
            world.Record(receipt.GuestId, "act", $"在{v.Name}吃了{receipt.Dish}", server?.Id);
            Persist();
        }
    }
    finally { gate.Release(); }

    if (receipt is null) return Results.Conflict(new { error = reason, reputation });
    return Results.Ok(new
    {
        venue = v.Id,
        dish = receipt.Dish,
        receipt.Price,
        guest = receipt.GuestId,
        servedBy = receipt.ServedById,
        effects = receipt.Effects,
        reputation,
        inLine = v.Shift?.Queue.Count ?? 0
    });
});

app.MapPost("/api/venue/shift/turn-away", async (ShiftVenueRequest r) =>
{
    var v = world.Venues.FirstOrDefault(x => x.Id == r.VenueId);
    if (v is null) return Results.NotFound(new { error = "没有这家店：" + r.VenueId });

    string? guest;
    string? reason;
    int reputation;
    await gate.WaitAsync();
    try
    {
        (guest, reason, reputation) = Shifts.TurnAwayHead(v);
        if (guest is not null) Persist();
    }
    finally { gate.Release(); }

    if (guest is null) return Results.Conflict(new { error = reason, reputation });
    return Results.Ok(new { turnedAway = guest, reputation, inLine = v.Shift?.Queue.Count ?? 0 });
});

app.MapPost("/api/venue/shift/close", async (ShiftVenueRequest r) =>
{
    var v = world.Venues.FirstOrDefault(x => x.Id == r.VenueId);
    if (v is null) return Results.NotFound(new { error = "没有这家店：" + r.VenueId });

    ShiftReport? report;
    string? reason;
    await gate.WaitAsync();
    try
    {
        (report, reason) = Shifts.Close(v);
        if (report is not null) Persist();
    }
    finally { gate.Release(); }

    if (report is null) return Results.Conflict(new { error = reason });
    return Results.Ok(report);
});

app.MapGet("/api/history", (int? take, string? scene) =>
{
    var n = Math.Clamp(take ?? 50, 1, 500);

    // With several scenes running at once, "the history" is ambiguous. Named, it is one room's
    // evening; unnamed, it stays the active scene's, which is what every older caller expects.
    if (scene is null) return Results.Ok(world.History.TakeLast(n));

    var target = world.Scenes.FirstOrDefault(s => s.Id == scene);
    if (target is null) return Results.NotFound(new { error = "没有这一幕：" + scene });
    return Results.Ok(target.History.TakeLast(n));
});

app.MapPost("/api/save", async () =>
{
    await gate.WaitAsync();
    try { Persist(); }
    finally { gate.Release(); }
    return Results.Ok(new { saved = store.Path });
});

app.Run();

record EnterRequest(string Id);
record CarryRequest(string Id, string Kind, string? From);
record PlotRequest(string Id, string BuildingId, string Kind, string? Name);
record BuildRequest(string Id, string Kind, string? OnPlot);
record SkyRequest(string? Weather, string? Season, int? Night);
record DivineRequest(string ById, string Kind, string? TargetId, string? Question, string? Advice);
record VenueOpenRequest(string OwnerId, string Name, string? Kind);
record VenueToggleRequest(string VenueId, bool Open);
record VenueMenuRequest(string VenueId, string Name, int Price, int? Effort, NeedRequest[]? Needs);
record NeedRequest(string Item, int Count);
record VenueStockRequest(string VenueId, string Item, int Count, int? UnitCost, string? PayerId);
record VenueStaffRequest(string VenueId, string StaffId, int? Wage, string? Role);
record VenueOrderRequest(string VenueId, string GuestId, string Dish, string? ServerId);
record VenueWagesRequest(string VenueId);
record ShiftVenueRequest(string VenueId);
record ShiftArriveRequest(string VenueId, string GuestId, string? Want);
record ShiftServeRequest(string VenueId, string? ServerId, string? Dish);
record FollowRequest(string SceneId);
record NarrateRequest(string SceneId, string Text, string? ActorId);
record OrderRequest(string ActorId, OrderKind Kind, string? TargetId, string? Text);
record AttachRequest(string Id, string? Mode, string? Endpoint, string? Model, string? ApiKey,
                     double? Temperature, int? MaxTokens, int? TimeoutMs);
record LoopStartRequest(int? IntervalMs, int? MaxRounds, int? UserWaitMs);
record WishRequest(WishKind Kind, string? SubjectId, string? TargetId);
record SceneOpenRequest(string[]? Cast, string? Title);
record AddressRequest(string ActorId, string TargetId, string Nickname);
record ActivityOpenRequest(string Name, string HostId, string Rules, string[]? Participants);
record ActivityMoveRequest(string ActivityId, string ActorId, string Move);
record ActivityCloseRequest(string ActivityId, string JudgedBy, string Verdict, ActivityEffectRequest[]? Effects);
record ActivityEffectRequest(string ActorId, int Poise, int Insight, int Renown, int Strain, int Reserve,
                             bool Ember, string? BondTarget, int BondDelta);
