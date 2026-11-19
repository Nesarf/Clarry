using System.Text;

namespace Clarry.Core;

/// <summary>
/// Everything an agent may see when the theatre asks what it does this round.
/// Only the public face of peers is included: insight is bought with Probe, not given away.
/// </summary>
public sealed class DriverContext
{
    public required Character Self { get; init; }
    public required World World { get; init; }
    public required int RoundNumber { get; init; }

    /// <summary>
    /// Which scene is asking. Optional only for older callers; when set, it decides the budget
    /// and it is the only history and cast this character may be shown. Two scenes may be
    /// running at once, and a person in one of them does not know what is happening in the other.
    /// </summary>
    public Scene? Scene { get; init; }

    /// <summary>How many points of action this character may spend this round.</summary>
    public int Budget => Scene is not null ? Scene.OrdersPerActor : World.OrdersPerActor;

    /// <summary>Tail of the shared history, oldest first.</summary>
    public IReadOnlyList<Turn> Recent { get; init; } = Array.Empty<Turn>();

    /// <summary>Who else is on stage right now, as a stranger would see them.</summary>
    public IReadOnlyList<Character> Peers { get; init; } = Array.Empty<Character>();

    /// <summary>Orders already declared this round by this character, used when asking again.</summary>
    public IReadOnlyList<Order> AlreadyDeclared { get; init; } = Array.Empty<Order>();

    /// <summary>
    /// What this character still remembers, heaviest first. Distinct from <see cref="Recent"/>:
    /// the recent turns are what just happened to everyone, while these are what stuck to this
    /// one person, filtered by their own memory style and faded by their own decay.
    /// </summary>
    public IReadOnlyList<Memory> Memories { get; init; } = Array.Empty<Memory>();
}

/// <summary>What a driver came back with. A failure is data, not an exception.</summary>
public sealed class DriverOutcome
{
    public List<Order> Orders { get; } = new();
    public string? Error { get; set; }
    public long ElapsedMs { get; set; }

    /// <summary>
    /// How many characters of prompt this round cost. Kept as a cheap local figure, but it is
    /// not the bill: characters say nothing about cache hits, and a constant system prompt is
    /// exactly what a provider's prompt cache exists for.
    /// </summary>
    public int PromptChars { get; set; }

    /// <summary>What the provider says it charged, when it says anything at all.</summary>
    public int? PromptTokens { get; set; }
    public int? CompletionTokens { get; set; }

    /// <summary>
    /// Prompt tokens served from the provider's cache. When this is high, the constant rules
    /// block is nearly free and shrinking it further is pointless -- which is the single most
    /// useful thing to know before optimising prompts.
    /// </summary>
    public int? CachedTokens { get; set; }

    public bool Ok => Error is null;

    public static DriverOutcome Fail(string error, long ms = 0, int promptChars = 0) =>
        new() { Error = error, ElapsedMs = ms, PromptChars = promptChars };
}

/// <summary>
/// One way of turning a character into orders. Drivers are adapters only: the resolver
/// never learns whether an order came from a model, a script, or the user's keyboard.
/// </summary>
public interface IAgentDriver
{
    string Kind { get; }
    Task<DriverOutcome> DriveAsync(DriverContext ctx, CancellationToken ct);
}

/// <summary>Replays a fixed script. Used by tests, by sample scenes, and as a fallback.</summary>
public sealed class ScriptedDriver : IAgentDriver
{
    private readonly Func<DriverContext, IEnumerable<Order>> _script;

    public ScriptedDriver(Func<DriverContext, IEnumerable<Order>> script) => _script = script;

    public string Kind => "scripted";

    public Task<DriverOutcome> DriveAsync(DriverContext ctx, CancellationToken ct)
    {
        var outcome = new DriverOutcome();
        outcome.Orders.AddRange(_script(ctx));
        return Task.FromResult(outcome);
    }

    /// <summary>The simplest useful script: alternate between speaking and acting.</summary>
    public static ScriptedDriver Alternating() => new(ctx => new[]
    {
        new Order
        {
            ActorId = ctx.Self.Id,
            Kind = ctx.RoundNumber % 2 == 1 ? OrderKind.Speak : OrderKind.Act,
            Text = ctx.RoundNumber % 2 == 1
                ? $"{ctx.Self.Name}看着屋里，没说什么。"
                : $"{ctx.Self.Name}换了个姿势。"
        }
    });
}

/// <summary>A parsed order draft, before it becomes an <see cref="Order"/>.</summary>
public sealed class OrderDraft
{
    public string? Kind { get; set; }
    public string? TargetId { get; set; }
    public string? Text { get; set; }

    /// <summary>Optional: an activity to call into being alongside this order.</summary>
    public ActivityDraft? Activity { get; set; }

    /// <summary>Optional: what this character will call the target from now on.</summary>
    public string? Nickname { get; set; }

    /// <summary>Optional: which apparatus a divination uses.</summary>
    public string? Tool { get; set; }

    /// <summary>Optional: a line left for the querent, if this character is an astrologer.</summary>
    public string? Advice { get; set; }
}

/// <summary>
/// Renders a round for a language model. Kept in one place so every transport -- an
/// OpenAI-compatible endpoint, a local runner, a hand-written script -- reads the same rules.
/// </summary>
public static class DriverPrompt
{
    public const string Rules = """
        You are playing a character in Clarry, a theatre where agents act for each other.
        Rounds are simultaneous: what you declare now takes effect only after the round is
        settled, together with everyone else's declarations. You cannot react to an action
        taken in the same round.

        Dice do not decide anything here. Do not ask for a roll. The only randomness allowed
        is inside a tabletop interlude, which you may propose as an Act.

        Declare one or two orders for this round. Reply with a JSON array and nothing else.

        Each element has this shape:
          {"kind": "<kind>", "targetId": <"someone else\'s id" or null>, "text": "<what is said or done>"}

        The kinds and their cost, in points of action:
          speak    0   say something aloud. text is the line.
          act      1   do something in the scene. text describes the deed.
          probe    1   read one other character. costs 1 reserve. it lands when your insight
                       outstrips their poise and their composure has worn thin; a failed
                       probe still sharpens you, so probing early is an investment.
          give     1   offer something to one other character. costs 1 reserve. warms the bond.
          withhold 0   keep silent on purpose. costs nothing but adds strain.
          reveal   2   bring a secret into the open. relieves strain, raises renown.
          divine   1   read what has not happened yet. Needs apparatus, and which one decides
                       what you see: tarot reads a person, crystal shows a room you are not in,
                       alembic turns a remedy out of the hour, observatory names the weather.
                       Add "tool" to choose one, "text" to say what you are asking about, and
                       "advice" to leave the querent a line they will act on.

        Those seven are not the limit of what you can do. When you want to run something they
        cannot express -- a drinking contest, a wager, a race, a trick, a shared ritual --
        attach an "activity" to an act and invent the rules yourself:

          {"kind": "act", "text": "<what you do to set it up>",
           "activity": {"name": "<short name>",
                        "rules": "<the rules, in your own words>",
                        "participants": ["id", "id"]}}

        The world records the rules as text and lets each participant submit a move. It does
        not check whether the rules are fair or even consistent: whoever judges the outcome
        declares it, and the world applies what they declare. A crooked judge is a scene.

        You may also decide what you call someone. Add "nickname" to any order that targets
        them, and from then on every line of yours should use that name instead of their id.
        Naming someone is how two characters start to sound close, so use it when it is true.

        Apparatus sits on three levels and they are not interchangeable. Cards and a crystal are
        carried, and go where you go. A plot is ground -- a back garden is one of them -- and a
        plot is not a building. An alembic is a basic building: it stands on a plot. An
        observatory is an extension, with no ground of its own, and may only be added to a basic
        building that already stands. Hold all four and you are an astrologer -- your readings
        reach further, the weather you name is the weather, and once you have practised enough,
        people pay.

        Three things worth knowing about your own state:
          - Saying a true thing out loud lights an ember in you. While it burns, your next
            act lands twice as hard and costs more strain. One use, and it is gone.
          - Strain that reaches 100 is paid for rather than capped: you forget the bond you
            held most thinly, and come back calmer and smaller. Mind your strain.
          - Reserve refills by itself. A round in which you spend none of it gives some
            back, so holding still is a real choice rather than a wasted turn.

        Stay inside your budget. An order you cannot afford is discarded and wasted.
        Write in the character's own voice. Do not narrate other characters' choices.
        """;

    public static string System(DriverContext ctx) => Rules;

    public static string User(DriverContext ctx)
    {
        var sb = new StringBuilder();
        var self = ctx.Self;

        sb.AppendLine($"Round {ctx.RoundNumber}. You are {self.Name} (id: {self.Id}).");
        // Which scene, not which world. Somewhere else in the same building other people may be
        // in the middle of their own round; this seat is not there and should not speak for them.
        if (ctx.Scene is { } scene)
        {
            var where = string.IsNullOrWhiteSpace(scene.Title) ? scene.Id : $"{scene.Id}（{scene.Title}）";
            sb.AppendLine($"You are in {where}. Only the people listed below are here with you.");
        }
        // The sky is shared by everyone under the roof, which is what makes it worth hearing
        // about from someone else -- and worth paying an astrologer to name.
        sb.AppendLine($"The sky: {ctx.World.Sky.Describe()}.");
        if (self.IsPlayer) sb.AppendLine("This seat belongs to a human; you are drafting for them and will be reviewed.");
        if (!string.IsNullOrWhiteSpace(self.Sheet.Role)) sb.AppendLine($"Role: {self.Sheet.Role}");
        if (!string.IsNullOrWhiteSpace(self.Sheet.Summary)) sb.AppendLine($"About you: {self.Sheet.Summary}");
        if (!string.IsNullOrWhiteSpace(self.Sheet.Voice)) sb.AppendLine($"Voice: {self.Sheet.Voice}");
        // The meter rather than the sheet: what they were declared as is history, what shows
        // now is who they are, and a trait that is climbing should be allowed to show.
        var traitsNow = self.Traits.Describe();
        if (!string.IsNullOrWhiteSpace(traitsNow))
            sb.AppendLine($"How you come across right now (0..100, louder is stronger): {traitsNow}");
        else if (self.Sheet.Traits.Count > 0)
            sb.AppendLine($"Traits: {string.Join("、", self.Sheet.Traits)}");
        if (self.Sheet.Goals.Count > 0) sb.AppendLine($"You want: {string.Join("; ", self.Sheet.Goals)}");
        if (self.Sheet.Fears.Count > 0) sb.AppendLine($"You fear: {string.Join("; ", self.Sheet.Fears)}");
        if (self.Sheet.Secrets.Count > 0) sb.AppendLine($"You keep hidden: {string.Join("; ", self.Sheet.Secrets)}");

        var a = self.Attributes;
        sb.AppendLine();
        sb.AppendLine("Your state right now, 0..100:");
        sb.AppendLine($"  poise {a.Poise}  insight {a.Insight}  renown {a.Renown}  strain {a.Strain}  reserve {a.Reserve}");
        if (self.Sheet.Relations.Count > 0)
        {
            sb.AppendLine("Bonds you already hold:");
            foreach (var (id, rel) in self.Sheet.Relations)
                sb.AppendLine($"  {id}: warmth {rel.Warmth:0.#}{(string.IsNullOrWhiteSpace(rel.Kind) ? "" : ", " + rel.Kind)}");
        }

        sb.AppendLine();
        if (ctx.Peers.Count == 0)
        {
            sb.AppendLine("Nobody else is on stage.");
        }
        else
        {
            sb.AppendLine("On stage with you:");
            foreach (var p in ctx.Peers)
            {
                // Role only. Their summary was the single fattest per-peer cost, and the model
                // learns who they are by watching them speak anyway.
                var bits = new List<string> { p.Id };
                if (!string.IsNullOrWhiteSpace(p.Sheet.Role)) bits.Add(p.Sheet.Role!);
                bits.Add(p.IsPlayer ? "(the human)" : "(an agent)");
                sb.AppendLine("  " + string.Join(" -- ", bits));
            }
        }

        if (self.Carried.Count > 0 || self.Buildings.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("What you hold, level by level:");

            foreach (var kit in self.Carried)
                sb.AppendLine($"  {ApparatusText.Of(kit.Kind)} -- 随身带着");

            // Basic buildings and the ground they brought with them. A workshop's backyard is
            // part of the workshop, so it prints under it rather than beside it.
            foreach (var b in self.Buildings.Where(x => x.Housing == Housing.Basic))
            {
                sb.AppendLine($"  {ApparatusText.Of(b.Kind)} -- 你名下的基础建筑");
                foreach (var plot in b.Plots)
                {
                    var standing = self.Buildings.Where(x => x.OnPlot == plot.Id)
                        .Select(x => ApparatusText.Of(x.Kind)).ToList();
                    var what = standing.Count == 0 ? "空着" : "立着" + string.Join("、", standing);
                    sb.AppendLine($"    {plot.Label}（场地）-- {what}");
                }
            }

            // Extensions last and separate: they are the only level that can end up standing on
            // nothing, and a prompt that hid that would let a model use a room it does not have.
            foreach (var b in self.Buildings.Where(x => x.Housing == Housing.Extension))
            {
                var on = "底下的场地已经没了";
                if (b.OnPlot is not null && Astrologer.FindPlot(self, b.OnPlot) is { } home)
                    on = $"建在你的{ApparatusText.Of(home.Host.Kind)}的{home.Plot.Label}里";
                sb.AppendLine($"  {ApparatusText.Of(b.Kind)}（扩建）-- {on}");
            }

            sb.AppendLine($"Astrology proficiency: {Astrologer.Proficiency(self)}.");
            var missing = Astrologer.Missing(self);
            if (Astrologer.IsAstrologer(self))
                sb.AppendLine("You hold all four and everything stands. You are an astrologer: people can come to you, and after enough readings they will pay.");
            else if (missing.Count > 0)
                sb.AppendLine("You are still missing: " + string.Join("、", missing.Select(ApparatusText.Of)) + ".");
            else
                sb.AppendLine("You hold all four, but something is not standing where it should be.");
        }

        if (self.Advice.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("What others have told you to do. You heard it; whether it is good is yours to judge:");
            foreach (var heard in self.Advice) sb.AppendLine($"  {heard.FromId}：{heard.Text}");
        }

        if (self.Attributes.Names.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("What you call them. Use these names in your lines, not their ids:");
            foreach (var (id, name) in self.Attributes.Names)
            {
                var who = ctx.Peers.FirstOrDefault(p => p.Id == id);
                sb.AppendLine($"  {id}{(who is null ? "" : "（" + who.Name + "）")} -- 你叫她/他「{name}」");
            }
        }

        sb.AppendLine();
        if (ctx.Memories.Count > 0)
        {
            sb.AppendLine("What you still remember, heaviest first. These are yours, not the room's:");
            foreach (var m in ctx.Memories)
            {
                var about = m.AboutId is null ? "" : $"（关于 {m.AboutId}）";
                sb.AppendLine($"  {m.Text}{about}");
            }
            sb.AppendLine();
        }

        if (ctx.Recent.Count == 0)
        {
            sb.AppendLine("Nothing has happened yet. You have the first word.");
        }
        else
        {
            sb.AppendLine("What just happened, oldest first:");
            foreach (var t in ctx.Recent)
            {
                var to = t.AddressedTo is null ? "" : $" -> {t.AddressedTo}";
                var roll = t.DiceRoll is null ? "" : $" [tabletop {t.DiceRoll}]";
                // Long lines are cut rather than dropped: the model needs to know a turn
                // happened and roughly what it was about, not to re-read every word of it.
                var line = t.Text ?? "";
                if (line.Length > 80) line = line[..80] + "…";
                sb.AppendLine($"  {t.ActorId} {t.Kind}{to}: {line}{roll}");
            }
        }

        if (ctx.AlreadyDeclared.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("You already declared this round:");
            foreach (var o in ctx.AlreadyDeclared)
                sb.AppendLine($"  {o.Kind.ToString().ToLowerInvariant()} (cost {o.Cost}) {o.Text}");
        }

        sb.AppendLine();
        sb.AppendLine($"You may spend {ctx.Budget} point(s) of action this round, {ctx.Budget} minus what you already declared.");
        sb.AppendLine("Reply with the JSON array now, nothing before or after it.");
        return sb.ToString();
    }
}

/// <summary>Pulls the first JSON array out of whatever a model actually wrote.</summary>
public static class OrderParser
{
    public static List<OrderDraft> ParseDrafts(string? raw)
    {
        var drafts = new List<OrderDraft>();
        if (string.IsNullOrWhiteSpace(raw)) return drafts;

        var start = raw.IndexOf('[');
        var end = raw.LastIndexOf(']');
        if (start < 0 || end <= start) return drafts;
        var slice = raw[start..(end + 1)];

        try
        {
            var parsed = System.Text.Json.JsonSerializer.Deserialize<List<OrderDraft>>(
                slice,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (parsed is not null) drafts.AddRange(parsed);
        }
        catch (System.Text.Json.JsonException)
        {
            // A model that wrote broken JSON gets no orders this round; the loop records it.
        }
        return drafts;
    }

    public static List<Order> ToOrders(string actorId, IEnumerable<OrderDraft> drafts)
    {
        var orders = new List<Order>();
        var seq = 0;
        foreach (var d in drafts)
        {
            if (!Enum.TryParse<OrderKind>(d.Kind, ignoreCase: true, out var kind)) continue;
            orders.Add(new Order
            {
                ActorId = actorId,
                Kind = kind,
                TargetId = string.IsNullOrWhiteSpace(d.TargetId) ? null : d.TargetId,
                Text = d.Text,
                Activity = d.Activity,
                Nickname = string.IsNullOrWhiteSpace(d.Nickname) ? null : d.Nickname.Trim(),
                Tool = ApparatusText.TryParse(d.Tool, out var tool) ? tool : null,
                Advice = string.IsNullOrWhiteSpace(d.Advice) ? null : d.Advice.Trim(),
                Sequence = seq++
            });
        }
        return orders;
    }
}
