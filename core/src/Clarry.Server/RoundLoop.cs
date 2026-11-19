using System.Collections.Concurrent;
using Clarry.Core;

namespace Clarry.Server;

/// <summary>One line of what the loop did, kept for the audience to read back.</summary>
public sealed record LoopLine(DateTimeOffset At, string Text);

/// <summary>A read-only view of the loop at the moment it was asked.</summary>
public sealed class LoopSnapshot
{
    public bool Running { get; init; }
    public bool AwaitingUser { get; init; }
    public string? AwaitingBecause { get; init; }

    /// <summary>Rounds run in the longest-running open scene.</summary>
    public int Round { get; init; }

    /// <summary>
    /// How many times the loop has advanced every open scene. One beat is one round in each
    /// scene, and the scenes advance together -- this is the count a cap applies to.
    /// </summary>
    public int Beats { get; init; }

    /// <summary>How many scenes are open right now.</summary>
    public int OpenScenes { get; init; }

    public string? StopReason { get; init; }
    public int IntervalMs { get; init; }
    public int MaxRounds { get; init; }

    /// <summary>Seats taken across every open scene, against the world's shared cap.</summary>
    public int SeatedAgents { get; init; }

    public int DrivenSeats { get; init; }

    /// <summary>
    /// Characters of prompt spent since this loop started. This is the number that decides
    /// whether any of this ever needs a faster language -- and where.
    /// </summary>
    public long PromptChars { get; init; }

    /// <summary>Billed prompt tokens since this loop started, as the provider reported them.</summary>
    public long PromptTokens { get; init; }

    /// <summary>
    /// How much of <see cref="PromptTokens"/> came from the provider's cache. High here means
    /// the constant rules block is already nearly free and shrinking it further buys nothing.
    /// </summary>
    public long CachedTokens { get; init; }

    /// <summary>The last settled round, whichever scene it belonged to.</summary>
    public RoundResult? LastResult { get; init; }
    public string? LastScene { get; init; }
    public IReadOnlyList<LoopLine> Log { get; init; } = Array.Empty<LoopLine>();
}

/// <summary>
/// The dramaturge. Each beat opens a round in every open scene, asks every seated agent with a
/// driver what it does, then settles those rounds one after another.
///
/// Asking runs in parallel across scenes -- two groups talking at once is the whole point of
/// having two scenes. Settling runs in series, because a settled order can reach a character who
/// is standing in a different scene (a gift warms the bond on both sides), and two settlements
/// touching the same person at once would make the outcome depend on scheduling.
///
/// A round that contains an order aimed at a human seat holds only the scene that human is
/// sitting in. The other scenes keep going, which is what "the world does not wait for you"
/// means once more than one scene exists.
///
/// Everything this class says out loud is addressed to the audience, so it is written in the
/// same language as the stage. Identifiers and transport kinds stay as they are.
/// </summary>
public sealed class RoundLoop
{
    private readonly World _world;
    private readonly WorldStore _store;
    private readonly MemoryStore _memories;
    private readonly ConcurrentDictionary<string, IAgentDriver> _drivers = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<LoopLine> _log = new();

    /// <summary>Shared with the HTTP surface so a manual call and a loop tick never interleave.</summary>
    private readonly SemaphoreSlim _gate;

    private CancellationTokenSource? _cts;
    private Task? _task;
    private volatile bool _running;
    private volatile string? _stopReason;
    private volatile string? _awaitingBecause;
    private int _intervalMs = 1_500;
    private int _maxRounds;
    private int _userWaitMs = 120_000;
    private int _beats;
    private RoundResult? _lastResult;
    private string? _lastScene;
    private long _promptChars;
    private long _promptTokens;
    private long _cachedTokens;

    private const int LogCap = 200;

    public RoundLoop(World world, WorldStore store, MemoryStore memories, SemaphoreSlim gate)
    {
        _world = world;
        _store = store;
        _memories = memories;
        _gate = gate;
    }

    public bool Running => _running;

    public void Attach(string characterId, IAgentDriver driver) => _drivers[characterId] = driver;

    public bool Detach(string characterId) => _drivers.TryRemove(characterId, out _);

    public IReadOnlyCollection<string> DrivenIds => _drivers.Keys.ToArray();

    private void Say(string text)
    {
        _log.Enqueue(new LoopLine(DateTimeOffset.Now, text));
        while (_log.Count > LogCap && _log.TryDequeue(out _)) { }
        Console.WriteLine("[loop] " + text);
    }

    public void Start(int intervalMs = 0, int maxRounds = 0, int userWaitMs = 0)
    {
        if (_running) return;
        if (intervalMs > 0) _intervalMs = Math.Clamp(intervalMs, 50, 600_000);
        _maxRounds = Math.Max(0, maxRounds);
        if (userWaitMs > 0) _userWaitMs = Math.Clamp(userWaitMs, 500, 3_600_000);
        _stopReason = null;
        _awaitingBecause = null;
        // The beat counter belongs to this run, not to the world: a cap is "this run gets N
        // beats", and a second start must not begin already over its own limit.
        _beats = 0;
        _cts = new CancellationTokenSource();
        _running = true;

        var cap = _maxRounds == 0 ? "不限拍数" : $"最多 {_maxRounds} 拍";
        Say($"开场：每 {_intervalMs} 毫秒推进一拍，每一拍让所有开着的幕各走一轮，{cap}");
        _task = Task.Run(() => RunAsync(_cts.Token));
    }

    public void Stop(string reason = "被要求收场")
    {
        if (!_running) { _stopReason = reason; return; }
        _stopReason = reason;
        _running = false;
        _cts?.Cancel();
        Say("收场：" + reason);
    }

    public LoopSnapshot Snapshot()
    {
        var open = _world.Scenes.Where(s => s.Open).ToList();
        return new()
        {
            Running = _running,
            AwaitingUser = _awaitingBecause is not null,
            AwaitingBecause = _awaitingBecause,
            Round = open.Count == 0 ? 0 : open.Max(s => s.Length),
            Beats = _beats,
            OpenScenes = open.Count,
            StopReason = _stopReason,
            IntervalMs = _intervalMs,
            MaxRounds = _maxRounds,
            SeatedAgents = _world.OccupiedCount,
            DrivenSeats = _drivers.Count,
            PromptChars = Interlocked.Read(ref _promptChars),
            PromptTokens = Interlocked.Read(ref _promptTokens),
            CachedTokens = Interlocked.Read(ref _cachedTokens),
            LastResult = _lastResult,
            LastScene = _lastScene,
            Log = _log.ToArray()
        };
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (_maxRounds > 0 && _beats >= _maxRounds)
                {
                    Stop("到了拍数上限");
                    return;
                }

                var scenes = _world.Scenes.Where(s => s.Open).ToArray();
                if (scenes.Length == 0)
                {
                    Stop("没有开着的幕了");
                    return;
                }

                _beats++;

                // 1. every open scene gets an open round. Short and under the gate, because the
                //    HTTP surface can open or close a scene at any moment.
                await _gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    foreach (var scene in scenes)
                        if (scene.CurrentRound is null or { Closed: true }) scene.BeginRound();
                }
                finally { _gate.Release(); }

                Say($"第 {_beats} 拍：{scenes.Length} 幕各开一轮");

                // 2. asking runs in parallel, across scenes as well as within them.
                await Task.WhenAll(scenes.Select(s => AskSceneAsync(s, ct))).ConfigureAwait(false);

                // 3. only the scene the human is sitting in can hold the beat.
                if (!await HoldForUserAsync(ct).ConfigureAwait(false)) return;

                // 4. settling runs in series. A settled order can reach across scenes -- a gift
                //    warms the bond on both sides -- so two settlements must not interleave.
                await _gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var converged = new List<Scene>();
                    foreach (var scene in scenes)
                    {
                        var round = scene.CurrentRound;
                        if (round is null || round.Closed) continue;

                        round.Close();
                        var result = RoundResolver.Resolve(round, _world, scene);
                        scene.TurnPointer = scene.Cast.NextTurn(scene.TurnPointer);
                        RecordMemories(round, scene);

                        _lastResult = result;
                        _lastScene = scene.Id;

                        var discarded = result.Ignored.Count == 0 ? "" : $"，丢弃 {result.Ignored.Count} 条";
                        Say($"{scene.Id} 第 {result.Round} 轮结算（{round.Orders.Count} 条行动{discarded}）");
                        foreach (var note in result.Notes.Take(4)) Say("　备注：" + note);

                        if (result.Converged) converged.Add(scene);
                    }

                    foreach (var scene in converged)
                    {
                        scene.Close();
                        Say($"{scene.Id} 收束了，这一幕散场（{scene.Cast.Active.Count} 位起身离开）");
                        // Closing a scene seats nobody: the people stay registered and free for
                        // another scene. Stage has no Clear(), and emptying it by hand is the
                        // honest way to say "everyone walked off".
                        foreach (var id in scene.Cast.Active.ToArray()) scene.Cast.Leave(id);
                    }

                    _store.Save(_world);
                }
                finally { _gate.Release(); }

                if (_world.Scenes.All(s => !s.Open))
                {
                    Stop("所有幕都收束了");
                    return;
                }

                if (_intervalMs > 0) await Task.Delay(_intervalMs, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Stop() already recorded why.
        }
        catch (Exception e)
        {
            _running = false;
            _stopReason = e.GetType().Name + "：" + e.Message;
            Say("循环出错：" + _stopReason);
        }
    }

    /// <summary>Asks every driven, non-human seat of one scene, all at once.</summary>
    private async Task AskSceneAsync(Scene scene, CancellationToken ct)
    {
        var round = scene.CurrentRound;
        if (round is null || round.Closed) return;

        var asks = new List<Task>();
        var asked = 0;

        foreach (var id in scene.Cast.Active)
        {
            if (!_drivers.TryGetValue(id, out var driver)) continue;
            var character = _world.Find(id);
            if (character is null || character.IsPlayer) continue;

            // Already declared the full budget this round: nothing left to ask.
            var declared = round.Orders.Where(o => o.ActorId == id).ToList();
            if (declared.Sum(o => o.Cost) >= scene.OrdersPerActor) continue;

            asked++;
            asks.Add(AskOneAsync(scene, round, character, driver, declared, ct));
        }

        if (asked > 0) Say($"{scene.Id} 第 {round.Number} 轮向 {asked} 位问了话");
        if (asks.Count > 0) await Task.WhenAll(asks).ConfigureAwait(false);
    }

    /// <summary>Settles a round into memories, then reports how much is left in each head.</summary>
    private void RecordMemories(Round round, Scene scene)
    {
        MemoryRecorder.Record(round, _world, _memories);

        var counts = string.Join("、", scene.Cast.Active.Select(id => $"{id} {_memories.Count(id)}"));
        if (counts.Length > 0) Say($"　{scene.Id} 记忆：" + counts);
    }

    /// <summary>
    /// Picks the history a character actually needs, instead of simply the last N turns.
    ///
    /// Measured first: each prompt ran about 3.5k characters and grew by ~460 per round as the
    /// tail filled up, so sixty-four agents would put a quarter-million characters per round
    /// through the meter. The expensive part is what gets sent, not how fast we build it --
    /// trimming a few thousand characters costs the CPU almost nothing.
    ///
    /// So a turn that touches this character is kept first, and the rest is filled from the
    /// most recent. Read from the scene, never the world: what happened in another scene did
    /// not happen in front of this one. Deterministic: same scene, same pick, every time.
    /// </summary>
    private static List<Turn> RecentFor(Scene scene, string selfId, int budget = 16)
    {
        var all = scene.History;
        var floor = Math.Max(0, all.Count - 32);
        var picked = new List<int>();

        for (var i = all.Count - 1; i >= floor && picked.Count < budget; i--)
        {
            var t = all[i];
            if (t.ActorId == selfId || t.AddressedTo == selfId) picked.Add(i);
        }

        for (var i = all.Count - 1; i >= floor && picked.Count < budget; i--)
        {
            if (!picked.Contains(i)) picked.Add(i);
        }

        picked.Sort();
        return picked.Select(i => all[i]).ToList();
    }

    private async Task AskOneAsync(Scene scene, Round round, Character character, IAgentDriver driver,
                                   List<Order> declared, CancellationToken ct)
    {
        var peers = scene.Cast.Active
            .Where(id => id != character.Id)
            .Select(id => _world.Find(id))
            .Where(c => c is not null)
            .Select(c => c!)
            .ToList();

        var style = MemoryStyle.For(character);
        var ctx = new DriverContext
        {
            Self = character,
            World = _world,
            Scene = scene,
            RoundNumber = round.Number,
            Recent = RecentFor(scene, character.Id),
            Peers = peers,
            AlreadyDeclared = declared,
            Memories = _memories.Recall(character.Id, style.Recall)
        };

        var outcome = await driver.DriveAsync(ctx, ct).ConfigureAwait(false);

        Interlocked.Add(ref _promptChars, outcome.PromptChars);
        if (outcome.PromptTokens is { } billedTotal) Interlocked.Add(ref _promptTokens, billedTotal);
        if (outcome.CachedTokens is { } cachedTotal) Interlocked.Add(ref _cachedTokens, cachedTotal);

        if (!outcome.Ok)
        {
            Say($"{scene.Id} 里 {character.Id} 没能开口（用了 {outcome.ElapsedMs} 毫秒，喂进 {outcome.PromptChars} 字）：{outcome.Error}");
            return;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (round.Closed) return;
            foreach (var order in outcome.Orders) round.Add(order);
            var kinds = string.Join("、", outcome.Orders.Select(o => OrderKindText.Of(o.Kind)));
            var bill = outcome.PromptTokens is { } billed
                ? $"，{billed} token 其中缓存 {outcome.CachedTokens?.ToString() ?? "未报"}"
                : "";
            Say($"{scene.Id} 里 {character.Id} 提了{kinds}（{outcome.ElapsedMs} 毫秒，喂进 {outcome.PromptChars} 字{bill}）");
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Holds the beat when an order points at the human seat, but only for the scene that human
    /// is actually sitting in. Returns false only when the loop was cancelled while waiting.
    /// </summary>
    private async Task<bool> HoldForUserAsync(CancellationToken ct)
    {
        var player = _world.Registry.Values.FirstOrDefault(c => c.IsPlayer);
        if (player is null) { _awaitingBecause = null; return true; }

        var scene = _world.Scenes.FirstOrDefault(s => s.Open && s.Cast.Active.Any(id => id == player.Id));
        var round = scene?.CurrentRound;
        if (scene is null || round is null) { _awaitingBecause = null; return true; }

        bool Addressed() => round.Orders.Any(o => o.TargetId == player.Id);
        bool PlayerSpoke() => round.Orders.Any(o => o.ActorId == player.Id);

        if (!Addressed() || PlayerSpoke()) { _awaitingBecause = null; return true; }

        var senders = round.Orders.Where(o => o.TargetId == player.Id).Select(o => o.ActorId).Distinct().ToArray();
        _awaitingBecause = $"{string.Join("、", senders)} 在 {scene.Id} 里问 {player.Id}";
        Say($"挂起 {scene.Id}：" + _awaitingBecause + "，别的幕照常推进");

        var waited = 0;
        while (waited < _userWaitMs && !ct.IsCancellationRequested && !round.Closed)
        {
            await Task.Delay(200, ct).ConfigureAwait(false);
            waited += 200;
            if (PlayerSpoke()) { _awaitingBecause = null; Say($"{player.Id} 在 {scene.Id} 接话了"); return true; }
        }
        if (ct.IsCancellationRequested) return false;

        _awaitingBecause = null;
        Say($"{player.Id} 超过 {_userWaitMs} 毫秒没有回应，{scene.Id} 这一轮按沉默结算");
        return true;
    }
}
