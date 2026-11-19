namespace Clarry.Server;

using Clarry.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

/// <summary>
/// Who is sitting in a seat, and what the theatre is doing while they sit there.
///
/// A driver is what turns a character into orders. Attaching one is how an outside agent -- a
/// hosted model, a local runner, anything with an HTTP face -- takes a seat. Nothing about this
/// layer knows which kind of agent it is talking to; it knows a kind name and a task.
///
/// The prompt endpoint earns its place here: every field on a DriverContext is only a claim about
/// what the model sees until it is printed, and a claim that is never printed is how something
/// meant for the model quietly fails to arrive.
/// </summary>
internal static class DriverEndpoints
{
    public static void MapDriverEndpoints(this WebApplication app, ClarryApp ctx)
    {
        app.MapPost("/api/driver/attach", (AttachRequest r) =>
        {
            var c = ctx.World.Find(r.Id);
            if (c is null) return Results.NotFound(new { error = "没有这个角色：" + r.Id });

            IAgentDriver driver;
            if (string.Equals(r.Mode, "scripted", StringComparison.OrdinalIgnoreCase))
            {
                // A delay is allowed here because a scripted driver that answers instantly can
                // never show whether the concurrency gate works. It is the only knob that makes
                // the ceiling observable from outside.
                driver = ScriptedDriver.Alternating(r.DelayMs ?? 0);
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
                    TimeoutMs = r.TimeoutMs ?? ctx.Options.DriverTimeoutMs
                });
            }

            ctx.Loop.Attach(r.Id, driver);
            return Results.Ok(new { attached = r.Id, kind = driver.Kind, driven = ctx.Loop.DrivenIds });
        });

        app.MapPost("/api/driver/detach", (EnterRequest r) =>
        {
            var removed = ctx.Loop.Detach(r.Id);
            return Results.Ok(new { detached = r.Id, removed, driven = ctx.Loop.DrivenIds });
        });

        app.MapGet("/api/driver/list", () => Results.Ok(new { driven = ctx.Loop.DrivenIds }));

        // The exact text a driver would receive for one character, right now. This is the only
        // honest way to check that something meant for the model actually reached it.
        app.MapPost("/api/driver/prompt", (PromptRequest r) =>
        {
            var c = ctx.World.Find(r.Id);
            if (c is null) return Results.NotFound(new { error = "没有这个角色：" + r.Id });

            var scene = ctx.World.Scenes.FirstOrDefault(s => s.Open && s.Cast.Active.Contains(c.Id))
                        ?? ctx.World.Focus();

            var peers = scene is null
                ? new List<Character>()
                : scene.Cast.Active
                    .Where(id => id != c.Id)
                    .Select(ctx.World.Find)
                    .Where(x => x is not null)
                    .Select(x => x!)
                    .ToList();

            var prompt = new DriverContext
            {
                Self = c,
                World = ctx.World,
                Scene = scene,
                RoundNumber = scene?.CurrentRound?.Number ?? 0,
                Recent = scene is null ? Array.Empty<Turn>() : scene.History.TakeLast(16).ToList(),
                Peers = peers,
                OpenPleas = Pleas.OpenFor(ctx.World, c.Id),
                OwnPleas = Pleas.By(ctx.World, c.Id).Where(p => p.State == PleaState.Open).ToList(),
                Memories = ctx.Memories.Recall(c.Id, MemoryStyle.For(c).Recall),
                // Whether the next time this person is asked, they will also be told about the
                // one thing this theatre never repeats. A glimpse that has already been put in
                // front of them is history and does not appear again -- which is why this reads
                // the pending flag rather than membership of the dictionary.
                Noticed = ctx.World.Glimpses.TryGetValue(c.Id, out var glimpse) && glimpse.ToldAt is null
            };

            var system = DriverPrompt.System(prompt);
            var user = DriverPrompt.User(prompt);
            var whole = system + "\n\n" + user;

            return Results.Ok(new
            {
                character = c.Id,
                scene = scene?.Id,
                systemChars = system.Length,
                userChars = user.Length,
                chars = whole.Length,
                approxTokens = whole.Length / 3,
                text = whole
            });
        });

        // --- the loop ---------------------------------------------------------------------

        app.MapPost("/api/loop/start", (LoopStartRequest? r) =>
        {
            ctx.Loop.Start(r?.IntervalMs ?? 0, r?.MaxRounds ?? 0, r?.UserWaitMs ?? 0);
            return Results.Ok(ctx.Loop.Snapshot());
        });

        app.MapPost("/api/loop/stop", (string? reason) =>
        {
            ctx.Loop.Stop(reason ?? "被要求收场");
            return Results.Ok(ctx.Loop.Snapshot());
        });

        app.MapGet("/api/loop/status", () => Results.Ok(ctx.Loop.Snapshot()));

        // The ledger: one row per beat, oldest first. Sum the columns to answer "how much did this
        // evening cost", sort by ElapsedMs to find the beat that went wrong, read Asked against
        // Quiet to see whether the theatre spent its calls on people who had something to say.
        app.MapGet("/api/runs", (int? take) =>
        {
            var n = Math.Clamp(take ?? 50, 1, 2000);
            var rows = ctx.Runs.Read(n);

            return Results.Ok(new
            {
                ledger = ctx.Runs.Location,
                rows,
                beats = rows.Count,
                asked = rows.Sum(r => r.Asked),
                quiet = rows.Sum(r => r.Quiet),
                promptChars = rows.Sum(r => r.PromptChars),
                promptTokens = rows.Sum(r => r.PromptTokens),
                cachedTokens = rows.Sum(r => r.CachedTokens),
                ignored = rows.Sum(r => r.Ignored),
                lapsed = rows.Sum(r => r.Lapsed),
                elapsedMs = rows.Sum(r => r.ElapsedMs)
            });
        });
    }
}

// --- requests ------------------------------------------------------------------------
record AttachRequest(string Id, string? Mode, string? Endpoint, string? Model, string? ApiKey,
                     double? Temperature, int? MaxTokens, int? TimeoutMs, int? DelayMs);
record PromptRequest(string Id);
record LoopStartRequest(int? IntervalMs, int? MaxRounds, int? UserWaitMs);
