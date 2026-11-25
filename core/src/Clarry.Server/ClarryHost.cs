namespace Clarry.Server;

using Clarry.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using System.Text.Json.Serialization;

/// <summary>
/// The theatre's whole HTTP surface, assembled once so more than one host can share it.
///
/// There are two of them now: the console server (<c>Program.cs</c>, one line) and the desktop
/// shell (<c>Clarry.Desktop</c>, a window with a WebView2 in it). Clarry is a standalone
/// program -- the DSH button was always an optional client, and building the window is what
/// made that literal.
///
/// The rule this file exists to keep: neither host owns a private copy of the world. Two
/// assemblies building two worlds is how a stage and a window disagree about who is on it.
/// </summary>
public static class ClarryHost
{
    /// <summary>
    /// Build the app without starting it. The caller decides how it runs: <c>Run()</c> blocks a
    /// console, <c>StartAsync()</c> lets a window own the process and stop it on close.
    /// </summary>
    public static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.ConfigureHttpJsonOptions(o =>
        {
            o.SerializerOptions.WriteIndented = true;
            o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase));
        });
        builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));
        var app = builder.Build();
        app.UseCors();

        // CLARRY_ROOT decides where stage/ and assets/ live, so a window launched from a build
        // directory still finds the repository. The desktop shell sets it before calling here.
        var root = Environment.GetEnvironmentVariable("CLARRY_ROOT") ?? @"D:\Clarry";
        var dataDir = Environment.GetEnvironmentVariable("CLARRY_DATA") ?? Path.Combine(root, "data");
        var stagePage = Path.Combine(root, "stage", "index.html");
        var assetsDir = Path.Combine(root, "assets");
        Directory.CreateDirectory(assetsDir);
        var store = new WorldStore(Path.Combine(dataDir, "world.json"));
        var snapshot = store.Load();
        var world = snapshot is null ? new World { Id = "main", Title = "" } : WorldStore.Restore(snapshot);

        // Every tunable in one place, read once. The same binary has to be able to be a laptop demo
        // and a full house, and the difference between those two is mostly how many drivers may be in
        // flight at the same moment.
        var options = ClarryOptions.FromEnvironment();

        // Memory lives in SQLite rather than the world file: it is queried by relevance, never read
        // whole, and it grows much faster than the world does.
        var memories = new MemoryStore(Path.Combine(dataDir, "memories.db"));

        // One gate for every mutation: a loop tick and a manual call take turns.
        var gate = new SemaphoreSlim(1, 1);

        // One row per beat on disk. The in-memory log answers "what just happened"; this answers "what
        // did last night look like", which is the question you ask once seats start costing money.
        var runs = new RunLog(Path.Combine(dataDir, "runs.jsonl"), options.ClosedSceneKeep * 200);
        var loop = new RoundLoop(world, store, memories, gate, options, runs);

        // Everything the endpoints share, in one object. Endpoint groups take this instead of closing
        // over a dozen locals, which is what lets a group move into its own file without rewriting it.
        var clarry = new ClarryApp
        {
            World = world,
            Store = store,
            Memories = memories,
            Gate = gate,
            Loop = loop,
            Runs = runs,
            Options = options,
            StagePage = stagePage,
            Loaded = snapshot is not null
        };

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

        // The endpoint groups, each in its own file under Endpoints/ and depending on nothing but
        // ClarryApp. Paths never changed when they moved; that is why 22 test suites were unaffected.
        app.MapWorldEndpoints(clarry);
        app.MapCharacterEndpoints(clarry);
        app.MapSceneEndpoints(clarry);
        app.MapPleaEndpoints(clarry);
        app.MapRoundEndpoints(clarry);
        app.MapDriverEndpoints(clarry);
        app.MapAgentEndpoints(clarry);
        app.MapWishEndpoints(clarry);

        // The four interludes moved to Endpoints/GameEndpoints.cs.
        app.MapGameEndpoints(clarry);

        // Ground, buildings, the sky and divination moved to Endpoints/DivinationEndpoints.cs.
        app.MapDivinationEndpoints(clarry);

        // Venues and shifts moved to Endpoints/VenueEndpoints.cs.
        app.MapVenueEndpoints(clarry);

        // The history sheet moved to Endpoints/SceneEndpoints.cs.

        app.MapPost("/api/save", async () =>
        {
            await gate.WaitAsync();
            try { Persist(); }
            finally { gate.Release(); }
            return Results.Ok(new { saved = store.Path });
        });

        return app;
    }
}
