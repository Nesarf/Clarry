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

    /// <summary>
    /// Requests from others that this character could answer. This is the social road out of
    /// being stuck, and the reason a plea is worth making at all.
    /// </summary>
    public IReadOnlyList<Plea> OpenPleas { get; init; } = Array.Empty<Plea>();

    /// <summary>What this character asked for and is still waiting on.</summary>
    public IReadOnlyList<Plea> OwnPleas { get; init; } = Array.Empty<Plea>();

    /// <summary>
    /// True only for the one person this round's glimpse landed on, and only for that round.
    /// It adds one paragraph to the prompt and nothing else in the world knows about it: no
    /// other character is told, no mechanic answers, and the order it produces is an ordinary
    /// line of dialogue like any other.
    /// </summary>
    public bool Noticed { get; init; }
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
    private readonly int _delayMs;

    public ScriptedDriver(Func<DriverContext, IEnumerable<Order>> script, int delayMs = 0)
    {
        _script = script;
        _delayMs = Math.Max(0, delayMs);
    }

    public string Kind => "scripted";

    public async Task<DriverOutcome> DriveAsync(DriverContext ctx, CancellationToken ct)
    {
        // A driver that answers without ever yielding cannot exercise a concurrency gate. The
        // delay exists so a test can make eight seats answer slowly enough to watch the ceiling
        // hold -- there is no other way to see a gate work.
        if (_delayMs > 0) await Task.Delay(_delayMs, ct).ConfigureAwait(false);

        var outcome = new DriverOutcome();
        outcome.Orders.AddRange(_script(ctx));
        return outcome;
    }

    /// <summary>The simplest useful script: alternate between speaking and acting.</summary>
    public static ScriptedDriver Alternating(int delayMs = 0) => new(ctx => new[]
    {
        new Order
        {
            ActorId = ctx.Self.Id,
            Kind = ctx.RoundNumber % 2 == 1 ? OrderKind.Speak : OrderKind.Act,
            Text = ctx.RoundNumber % 2 == 1
                ? $"{ctx.Self.Name}看着屋里，没说什么。"
                : $"{ctx.Self.Name}换了个姿势。"
        }
    }, delayMs);
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

    /// <summary>Optional: what this character makes of the target, in their own words.</summary>
    public string? Impression { get; set; }

    /// <summary>Optional: which apparatus a divination uses.</summary>
    public string? Tool { get; set; }

    /// <summary>Optional: a line left for the querent, if this character is an astrologer.</summary>
    public string? Advice { get; set; }

    /// <summary>Optional: which open request this order answers.</summary>
    public string? PleaId { get; set; }

    /// <summary>Optional, on a plea: one of the six shapes of wall.</summary>
    public string? Blocked { get; set; }

    /// <summary>Optional, on a plea: what would close the gap, in this character's own words.</summary>
    public string? Needs { get; set; }
}

/// <summary>
/// Renders a round for a language model. Kept in one place so every transport -- an
/// OpenAI-compatible endpoint, a local runner, a hand-written script -- reads the same rules.
/// </summary>
public static class DriverPrompt
{
    public const string Rules = """
        You are a person living in a house with other people.
        Rounds are simultaneous: what you declare now takes effect only after the round is
        settled, together with everyone else's declarations. You cannot react to an action
        taken in the same round.

        You are not playing a character, and nobody is playing one for you. There is no word
        in your mouth for what is behind a face: if you catch yourself reaching for one, say
        what the person is doing instead. Nothing outside this house is any of your business,
        and the other people here are exactly who they appear to be.

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
          answer   0   answer one open request. put its id in "pleaId". this is a promise, not a
                       transfer: whatever they asked for still has to be handed over separately.
          plea     0   ask for one specific thing you cannot get alone. "blocked" names the shape
                       of the wall -- object, skill, person, place, permit, money -- and "needs"
                       says what would close it, in your own words. It stays open three rounds:
                       another person may answer it, a shrine may, or nobody may and you go
                       another way. All three are real endings, and the third is the common one.

        Those eight are not the limit of what you can do. When you want to run something they
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

        You decide what you make of people too. Add "impression" to an order that targets them:
        one short line, in your own words, the way you would describe them to somebody who had
        never met them. It stays yours -- nobody else sees it, and nothing checks whether it is
        fair. It may be wrong, and being wrong about someone is a story.

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

        sb.AppendLine($"Round {ctx.RoundNumber}.");

        // Who she is, in her own words. Everything below this line is what other people know,
        // and the two are allowed to disagree -- that gap is where most of a character lives.
        var selfImage = string.IsNullOrWhiteSpace(self.Sheet.SelfImage)
            ? "someone who lives here"
            : self.Sheet.SelfImage!;
        sb.AppendLine($"You are {self.Name} -- {selfImage}.");
        sb.AppendLine("That is who you are. Whatever else is written about you below is what other people know, which is not always the same thing.");

        // IsPlayer is loading information -- who is holding this seat -- and never reaches the
        // prompt. On stage that character is simply a person like the rest, and telling the model
        // there is an audience for its draft would hand it the fourth wall for free.
        if (!string.IsNullOrWhiteSpace(self.Sheet.Role)) sb.AppendLine($"What people call your line of work: {self.Sheet.Role}");
        if (!string.IsNullOrWhiteSpace(self.Sheet.Summary)) sb.AppendLine($"What others know about you: {self.Sheet.Summary}");
        if (!string.IsNullOrWhiteSpace(self.Sheet.Voice)) sb.AppendLine($"How you sound: {self.Sheet.Voice}");

        // Which scene, not which world. Somewhere else in the same building other people may be
        // in the middle of their own round; this seat is not there and should not speak for them.
        if (ctx.Scene is { } scene)
        {
            // The scene's id is bookkeeping; a person knows the room by its name, or not at all.
            var where = string.IsNullOrWhiteSpace(scene.Title)
                ? "a room with no name on the door"
                : $"a place called {scene.Title}";
            sb.AppendLine($"You are in {where}. Only the people listed below are here with you.");
        }

        // The sky is shared by everyone under the roof, which is what makes it worth hearing
        // about from someone else -- and worth paying an astrologer to name.
        sb.AppendLine($"The sky: {ctx.World.Sky.Describe()}.");
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
                // Name first, id second: the name is what a person is called, the id is what a
                // ledger calls them. Both are needed -- orders target ids -- but only one of
                // them is who they are.
                var bits = new List<string> { $"{p.Name}（{p.Id}）" };
                if (!string.IsNullOrWhiteSpace(p.Sheet.Role)) bits.Add(p.Sheet.Role!);
                // No tag for what is driving them. A face at this table is a person; whether a
                // model, a script, or the audience is behind it belongs to the loading dock.
                bits.Add(p.IsPlayer ? "(在座)" : "(在场)");
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

        // Being stuck is public on purpose. This block is the social road out of it: somebody
        // else's problem, visible to whoever could do something about it.
        if (ctx.OpenPleas.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Others are stuck and have said so. Answering one is a way to matter here:");
            foreach (var plea in ctx.OpenPleas)
            {
                var who = plea.ToId is null ? "谁都可以" : "指名给你";
                sb.AppendLine($"  {plea.Id}  {plea.FromId} {BlockedText.Of(plea.Blocked)}：{plea.Needs}（{who}，还剩 {plea.Lifetime} 轮）");
                sb.AppendLine($"        「{plea.Text}」");
            }
            sb.AppendLine("To answer one: kind answer, pleaId set to it, targetId set to whoever asked.");
        }

        if (ctx.World.Shrines.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Places where a request can be left, and who is said to hear them:");
            foreach (var shrine in ctx.World.Shrines)
            {
                var god = ctx.World.Deities.FirstOrDefault(d => d.Id == shrine.DeityId);
                if (god is null) continue;
                sb.AppendLine($"  {shrine.Name} -- {god.Name}，司掌{god.Domain}（应过 {god.Answered} 次）");
            }
            sb.AppendLine("A request left at one of these is addressed to that god. Whether it answers is not yours to know.");
        }

        if (ctx.OwnPleas.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("What you asked for and are still waiting on:");
            foreach (var plea in ctx.OwnPleas)
                sb.AppendLine($"  {plea.Id}  {BlockedText.Of(plea.Blocked)}：{plea.Needs}（还剩 {plea.Lifetime} 轮）");
            sb.AppendLine("If nobody answers, you go another way. That is allowed, and it is not a failure.");
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

        if (self.Attributes.Impressions.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("How you see them so far, in your own words. This is your judgement, not a fact:");
            foreach (var (id, impression) in self.Attributes.Impressions)
            {
                var who = ctx.Peers.FirstOrDefault(p => p.Id == id);
                sb.AppendLine($"  {id}{(who is null ? "" : "（" + who.Name + "）")} -- {impression}");
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

        // This goes in before the closing instructions on purpose: an aside is something that
        // happens to a person, and what they do about it is still their round to declare.
        if (ctx.Noticed)
        {
            sb.AppendLine();
            sb.AppendLine(GlimpseRules.Aside);
        }

        sb.AppendLine();
        sb.AppendLine($"You may spend {ctx.Budget} point(s) of action this round, {ctx.Budget} minus what you already declared.");
        sb.AppendLine("Reply with the JSON array now, nothing before or after it.");
        return sb.ToString();
    }
}

/// <summary>Pulls the orders out of whatever a model actually wrote.</summary>
public static class OrderParser
{
    public static List<OrderDraft> ParseDrafts(string? raw)
    {
        var drafts = new List<OrderDraft>();
        if (string.IsNullOrWhiteSpace(raw)) return drafts;

        var options = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        // First try the shape the prompt asks for: one array, nothing around it.
        var start = raw.IndexOf('[');
        var end = raw.LastIndexOf(']');
        if (start >= 0 && end > start && TryParse(raw[start..(end + 1)], options, drafts))
            return drafts;

        // Some models answer with several arrays in a row, one per thing they want to do.
        // Reading only the first array drops half the turn; taking the outermost bracket pair
        // spans all of them and parses as nothing at all, which drops the whole turn -- and
        // that is the bug this branch exists for: the model wrote a perfectly good turn and
        // the theatre recorded a silence. So slice them apart and read each one.
        drafts.Clear();
        foreach (var piece in SliceArrays(raw)) TryParse(piece, options, drafts);
        return drafts;
    }

    private static bool TryParse(string json, System.Text.Json.JsonSerializerOptions options, List<OrderDraft> into)
    {
        try
        {
            var parsed = System.Text.Json.JsonSerializer.Deserialize<List<OrderDraft>>(json, options);
            if (parsed is null || parsed.Count == 0) return false;
            into.AddRange(parsed);
            return true;
        }
        catch (System.Text.Json.JsonException)
        {
            // A model that wrote broken JSON gets no orders from that piece; the loop records it.
            return false;
        }
    }

    /// <summary>
    /// Every top-level array in the text, brackets balanced and string literals respected so a
    /// ']' inside somebody's dialogue does not end a turn early.
    /// </summary>
    private static IEnumerable<string> SliceArrays(string raw)
    {
        var depth = 0;
        var inString = false;
        var escaped = false;
        var open = -1;

        for (var i = 0; i < raw.Length; i++)
        {
            var ch = raw[i];

            if (inString)
            {
                if (escaped) escaped = false;
                else if (ch == '\\') escaped = true;
                else if (ch == '"') inString = false;
                continue;
            }

            if (ch == '"') { inString = true; continue; }

            if (ch == '[')
            {
                if (depth == 0) open = i;
                depth++;
            }
            else if (ch == ']' && depth > 0)
            {
                depth--;
                if (depth == 0 && open >= 0)
                {
                    yield return raw[open..(i + 1)];
                    open = -1;
                }
            }
        }
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
                Impression = string.IsNullOrWhiteSpace(d.Impression) ? null : d.Impression.Trim(),
                Tool = ApparatusText.TryParse(d.Tool, out var tool) ? tool : null,
                Advice = string.IsNullOrWhiteSpace(d.Advice) ? null : d.Advice.Trim(),
                PleaId = string.IsNullOrWhiteSpace(d.PleaId) ? null : d.PleaId.Trim(),
                Blocked = BlockedText.TryParse(d.Blocked, out var blocked) ? blocked : null,
                Needs = string.IsNullOrWhiteSpace(d.Needs) ? null : d.Needs.Trim(),
                Sequence = seq++
            });
        }
        return orders;
    }
}
