using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Clarry.Core;

/// <summary>
/// The on-disk shape of one scene. A plain DTO for the same reason the world is: the live
/// <see cref="Scene"/> holds a Stage with private state and a Round ledger that is not meant
/// to be resumed after a restart.
/// </summary>
public sealed class SceneSnapshot
{
    public string Id { get; set; } = "scene-1";
    public int Number { get; set; } = 1;
    public string Title { get; set; } = "";
    public List<string> Cast { get; set; } = new();
    public List<Turn> History { get; set; } = new();
    public int OrdersPerActor { get; set; } = 2;
    public int CluesResolved { get; set; }
    public int ClueGoal { get; set; } = 3;
    public int TurnPointer { get; set; }
    public bool Open { get; set; } = true;
    public DateTimeOffset OpenedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ClosedAt { get; set; }

    /// <summary>When something last happened here. Saved, so the camera does not jump on restart.</summary>
    public DateTimeOffset LastChangedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// The on-disk shape of a world. Deliberately a plain DTO: the live World holds a Stage with
/// private state, and serialising it directly would quietly drop the stage.
///
/// <see cref="StageActive"/> and <see cref="History"/> are the pre-scene layout. They stay so
/// that a world saved before scenes existed still loads; new saves leave them empty and put
/// everything in <see cref="Scenes"/>.
/// </summary>
public sealed class WorldSnapshot
{
    public string Id { get; set; } = "main";
    public string Title { get; set; } = "";
    public string? Genre { get; set; }
    public string? Premise { get; set; }
    public int OrdersPerActor { get; set; } = 2;
    public int CluesResolved { get; set; }
    public int ClueGoal { get; set; } = 3;
    public int TurnPointer { get; set; }
    public List<Character> Characters { get; set; } = new();
    public List<SceneSnapshot> Scenes { get; set; } = new();
    public int WishBudget { get; set; } = 3;
    public int WishesLeft { get; set; } = 3;
    public List<Wish> Wishes { get; set; } = new();
    public List<Activity> Activities { get; set; } = new();
    public List<Venue> Venues { get; set; } = new();
    public List<Plea> Pleas { get; set; } = new();
    public int NextPleaNumber { get; set; } = 1;
    public List<Shrine> Shrines { get; set; } = new();
    public List<Deity> Deities { get; set; } = new();

    /// <summary>The sky over the whole world. Null in saves written before it existed.</summary>
    public Sky? Sky { get; set; }

    public string? ForcedPairA { get; set; }
    public string? ForcedPairB { get; set; }

    /// <summary>A scene the camera is pinned to. Null means it follows whatever moved last.</summary>
    public string? FocusScene { get; set; }
    public DateTimeOffset SavedAt { get; set; } = DateTimeOffset.UtcNow;

    // Legacy layout, read-only in practice.
    public List<string> StageActive { get; set; } = new();
    public List<Turn> History { get; set; } = new();
}

/// <summary>
/// Reads and writes one world to JSON. Every write goes through a temp file and a rename,
/// so a crash mid-write cannot leave a half world on disk.
/// </summary>
public sealed class WorldStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Character names are the story. Escaping them to \uXXXX keeps the file correct but
        // makes it unreadable, and a save file you cannot read is a save file you cannot check.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public string Path { get; }

    public WorldStore(string path) => Path = path;

    public WorldSnapshot? Load()
    {
        if (!File.Exists(Path)) return null;
        var json = File.ReadAllText(Path);
        return JsonSerializer.Deserialize<WorldSnapshot>(json, Options);
    }

    public void Save(World world)
    {
        var snap = Snapshot(world);
        var dir = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = Path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(snap, Options));
        File.Move(tmp, Path, overwrite: true);
    }

    public static WorldSnapshot Snapshot(World world) => new()
    {
        Id = world.Id,
        Title = world.Title,
        Genre = world.Genre,
        Premise = world.Premise,
        Characters = world.Registry.Values.ToList(),
        Scenes = world.Scenes.Select(s => new SceneSnapshot
        {
            Id = s.Id,
            Number = s.Number,
            Title = s.Title,
            Cast = s.Cast.Active.ToList(),
            History = s.History.ToList(),
            OrdersPerActor = s.OrdersPerActor,
            CluesResolved = s.CluesResolved,
            ClueGoal = s.ClueGoal,
            TurnPointer = s.TurnPointer,
            Open = s.Open,
            OpenedAt = s.OpenedAt,
            ClosedAt = s.ClosedAt,
            LastChangedAt = s.LastChangedAt
        }).ToList(),
        WishBudget = world.WishBudget,
        WishesLeft = world.WishesLeft,
        Wishes = world.Wishes.ToList(),
        Activities = world.Activities.ToList(),
        Venues = world.Venues.ToList(),
        Pleas = world.Pleas.ToList(),
        NextPleaNumber = world.NextPleaNumber,
        Shrines = world.Shrines.ToList(),
        Deities = world.Deities.ToList(),
        Sky = world.Sky,
        ForcedPairA = world.ForcedPairA,
        ForcedPairB = world.ForcedPairB,
        FocusScene = world.FocusScene,
        SavedAt = DateTimeOffset.UtcNow
    };

    public static World Restore(WorldSnapshot snap)
    {
        var world = new World
        {
            Id = snap.Id,
            Title = snap.Title,
            Genre = snap.Genre,
            Premise = snap.Premise
        };

        // The constructor always makes one scene; a restore replaces it with what was saved.
        world.Scenes.Clear();

        foreach (var c in snap.Characters) world.Add(c);

        if (snap.Scenes.Count > 0)
        {
            foreach (var s in snap.Scenes)
            {
                var scene = new Scene
                {
                    Id = s.Id,
                    Number = s.Number,
                    Title = s.Title,
                    OpenedAt = s.OpenedAt
                };
                scene.OrdersPerActor = s.OrdersPerActor;
                scene.CluesResolved = s.CluesResolved;
                scene.ClueGoal = s.ClueGoal;
                scene.TurnPointer = s.TurnPointer;
                scene.Open = s.Open;
                scene.ClosedAt = s.ClosedAt;
                scene.Touch(s.LastChangedAt);
                foreach (var id in s.Cast) scene.Cast.Enter(id);
                scene.History.AddRange(s.History);
                world.Scenes.Add(scene);
            }
        }
        else
        {
            // A world saved before scenes existed carries one flat cast and one flat history.
            var scene = new Scene { Id = "scene-1", Number = 1 };
            scene.OrdersPerActor = snap.OrdersPerActor;
            scene.CluesResolved = snap.CluesResolved;
            scene.ClueGoal = snap.ClueGoal;
            scene.TurnPointer = snap.TurnPointer;
            foreach (var id in snap.StageActive) scene.Cast.Enter(id);
            scene.History.AddRange(snap.History);
            world.Scenes.Add(scene);
        }

        // WishBudget is a design number, not a saved one. An old world that remembered three
        // must not keep a budget the design has since lowered; what is left is restored, and
        // what the maximum is comes from the code.
        world.WishesLeft = Math.Min(snap.WishesLeft, world.WishBudget);
        world.Wishes.AddRange(snap.Wishes);
        world.Activities.AddRange(snap.Activities);
        world.Venues.AddRange(snap.Venues);
        world.Pleas.AddRange(snap.Pleas);
        world.NextPleaNumber = Math.Max(snap.NextPleaNumber, world.Pleas.Count + 1);

        // The constructor seeds one shrine so a new world is usable at once; a restore replaces
        // that with whatever was saved, and keeps the default when the save predates shrines.
        if (snap.Shrines.Count > 0)
        {
            world.Shrines.Clear();
            world.Shrines.AddRange(snap.Shrines);
        }
        if (snap.Deities.Count > 0)
        {
            world.Deities.Clear();
            world.Deities.AddRange(snap.Deities);
        }
        world.Sky = snap.Sky ?? new Sky();
        world.ForcedPairA = snap.ForcedPairA;
        world.ForcedPairB = snap.ForcedPairB;
        world.FocusScene = snap.FocusScene;
        return world;
    }
}
