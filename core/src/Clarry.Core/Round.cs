namespace Clarry.Core;

/// <summary>
/// One simultaneous turn. Orders accumulate while the round is open; nothing takes effect
/// until <see cref="RoundResolver"/> settles it.
/// </summary>
public sealed class Round
{
    public required int Number { get; init; }
    public List<Order> Orders { get; } = new();
    public bool Closed { get; private set; }
    public RoundResult? Result { get; private set; }

    public void Add(Order order)
    {
        if (Closed) throw new InvalidOperationException($"第 {Number} 轮已经关闭");
        Orders.Add(order);
    }

    public void Close() => Closed = true;

    public void Settle(RoundResult result)
    {
        if (!Closed) throw new InvalidOperationException("结算之前必须先关闭这一轮");
        Result = result;
    }
}

/// <summary>
/// What a settled round produced. Read by the audience, by idle agents, and by tests.
/// </summary>
public sealed class RoundResult
{
    public required int Round { get; init; }
    public Dictionary<string, AttributeDelta> Deltas { get; } = new(StringComparer.Ordinal);
    public List<string> Revealed { get; } = new();
    public List<Order> Ignored { get; } = new();
    public List<string> Notes { get; } = new();
    public bool Converged { get; set; }
}

/// <summary>
/// Settles a round in a fixed order so the same order set always yields the same outcome.
///   1 collect  2 visibility(probe)  3 reserve  4 bonds  5 state
///   5.4 recharge  5.5 ember  5.6 hollowing  5.7 activities  5.8 naming  5.9 traits  6 clues  7 convergence  8 archive
/// No dice. The world never rolls; characters may, inside a tabletop session of their own.
/// </summary>
public static class RoundResolver
{
    /// <summary>
    /// How much reach a probe needs to land: the asker's insight, plus the target's missing
    /// poise, plus half their strain. At a fresh 50/50/20 that comes to 85.5, just short --
    /// so reading someone takes a second attempt, or a target who has been moving and
    /// straining. Probing never rolls; it accumulates.
    /// </summary>
    public const double ProbeThreshold = 87.0;

    /// <summary>
    /// What a divination uses when the order does not say. Cards first, because that is what a
    /// reader reaches for without thinking about it, then whatever building they happen to own.
    /// </summary>
    private static ApparatusKind GuessTool(Character c) =>
        c.Carried.Select(k => k.Kind)
         .Concat(c.Buildings.Select(b => b.Kind))
         .FirstOrDefault(ApparatusKind.Tarot);

    public static RoundResult Resolve(Round round, World world) => Resolve(round, world, world.Active);

    /// <summary>
    /// Settles one round belonging to one scene.
    ///
    /// The scene matters at four points and nowhere else: the action budget, the clue counter
    /// that decides convergence, where the settled lines are archived, and which goal the
    /// convergence is measured against. Everything touching a character -- attributes, bonds,
    /// traits, memories -- belongs to that person and travels with them between scenes, which
    /// is exactly the distinction the split was for.
    /// </summary>
    public static RoundResult Resolve(Round round, World world, Scene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        var result = new RoundResult { Round = round.Number };

        // 1. collect -- keep declaration order for determinism, cap by per-actor budget
        var budget = new Dictionary<string, int>(StringComparer.Ordinal);
        var accepted = new List<Order>();
        foreach (var order in round.Orders)
        {
            budget.TryGetValue(order.ActorId, out var spent);
            if (spent + order.Cost > scene.OrdersPerActor)
            {
                result.Ignored.Add(order);
                result.Notes.Add($"{order.ActorId} 这轮的行动点不够，{OrderKindText.Of(order.Kind)}没能提出");
                continue;
            }
            var actor = world.Find(order.ActorId);
            if (actor is null)
            {
                result.Ignored.Add(order);
                result.Notes.Add($"台上没有 {order.ActorId} 这个角色");
                continue;
            }
            if (order.SpendsReserve && actor.Attributes.Reserve < order.Cost)
            {
                result.Ignored.Add(order);
                result.Notes.Add($"{order.ActorId} 余力不足，{OrderKindText.Of(order.Kind)}没能出手");
                continue;
            }
            budget[order.ActorId] = spent + order.Cost;
            accepted.Add(order);
        }

        // 2. visibility -- probes resolve first, they decide who sees what.
        // A probe is deterministic, not random: what decides it is how much insight the
        // asker holds and how little poise the target is still keeping. A rattled or tired
        // character is simply easier to read, and a failed probe still sharpens the prober,
        // so probing is an investment rather than a coin toss.
        foreach (var order in accepted.Where(o => o.Kind == OrderKind.Probe))
        {
            var actor = world.Find(order.ActorId)!;
            actor.Attributes.Reserve -= order.Cost;
            actor.Attributes.Insight = Attributes.Clamp(actor.Attributes.Insight + 1);

            var target = order.TargetId is null ? null : world.Find(order.TargetId);
            var poise = target?.Attributes.Poise ?? 50;
            var strain = target?.Attributes.Strain ?? 20;
            var reach = actor.Attributes.Insight + (100 - poise) / 2.0 + strain / 2.0;

            if (target is not null && reach >= ProbeThreshold)
            {
                result.Revealed.Add($"{order.ActorId} 看穿了 {order.TargetId}");
                actor.Attributes.Insight = Attributes.Clamp(actor.Attributes.Insight + 2);
                // Reading someone successfully teaches you about people, not just this one.
                actor.Traits.Shift("敏锐", 2);
                scene.CluesResolved++;
                result.Notes.Add($"{order.ActorId} 看穿了 {order.TargetId}（触及 {reach:0.#}）");
            }
            else
            {
                result.Notes.Add($"{order.ActorId} 试探 {order.TargetId ?? "空处"}，什么也没看出来（触及 {reach:0.#}，需要 {ProbeThreshold}）");
            }
        }

        // 3+4. reserve + bonds
        foreach (var order in accepted)
        {
            var actor = world.Find(order.ActorId)!;
            switch (order.Kind)
            {
                case OrderKind.Give when order.TargetId is not null:
                    actor.Attributes.Reserve -= order.Cost;
                    actor.Attributes.ShiftBond(order.TargetId, +4);
                    world.Find(order.TargetId)?.Attributes.ShiftBond(order.ActorId, +6);
                    break;
                case OrderKind.Withhold:
                    actor.Attributes.Strain = Attributes.Clamp(actor.Attributes.Strain + 1);
                    break;
            }
        }

        // 5. state -- poise/renown/strain move from what actually happened
        foreach (var order in accepted)
        {
            var actor = world.Find(order.ActorId)!;
            switch (order.Kind)
            {
                case OrderKind.Speak:
                    actor.Attributes.Poise = Attributes.Clamp(actor.Attributes.Poise + 1);
                    break;
                case OrderKind.Act:
                    // A burning ember makes the bold move land twice as hard, and it costs
                    // more than it gave. One use, then it is gone.
                    if (actor.Attributes.Ember)
                    {
                        actor.Attributes.Renown = Attributes.Clamp(actor.Attributes.Renown + 4);
                        actor.Attributes.Strain = Attributes.Clamp(actor.Attributes.Strain + 7);
                        actor.Attributes.Ember = false;
                        result.Notes.Add($"{order.ActorId} 带着余火出手：声望 +4，压力 +7，火烧尽了");
                    }
                    else
                    {
                        actor.Attributes.Renown = Attributes.Clamp(actor.Attributes.Renown + 2);
                        actor.Attributes.Strain = Attributes.Clamp(actor.Attributes.Strain + 1);
                    }
                    break;
                case OrderKind.Reveal:
                    actor.Attributes.Renown = Attributes.Clamp(actor.Attributes.Renown + 6);
                    actor.Attributes.Strain = Attributes.Clamp(actor.Attributes.Strain - 8);
                    result.Revealed.Add($"{order.ActorId} 说了实话");
                    break;
            }
        }

        // 5.4 recharge -- Reserve is this theatre's sync rate rather than a stamina bar.
        // The distinction matters: a stamina bar is drained once and then gates everything,
        // while a pool that refills while you are not spending it makes patience a real
        // option instead of a slow death. Restraint is rewarded, not punished.
        foreach (var id in world.Registry.Keys.ToList())
        {
            var a = world.Find(id)!.Attributes;
            if (a.Reserve >= 100) continue;
            if (accepted.Any(o => o.ActorId == id && o.SpendsReserve)) continue;
            a.Reserve = Attributes.Clamp(a.Reserve + 3);
        }

        // 5.5 ember -- saying a true thing out loud lights you. The light arrives only after
        // the round is settled, so it is spent in the round that follows; nobody gets to be
        // strengthened by the very sentence they are still speaking.
        foreach (var order in accepted.Where(o => o.Kind == OrderKind.Reveal))
        {
            var actor = world.Find(order.ActorId)!;
            if (actor.Attributes.Ember) continue;
            actor.Attributes.Ember = true;
            result.Notes.Add($"{order.ActorId} 被自己的话点着了，带着余火走进下一轮");
        }

        // 5.6 hollowing -- pressure that reaches the ceiling is not capped, it is paid for.
        // Taken as an image rather than as a system: what is lost is not humanity in the
        // Dark Souls sense but a specific person. The character forgets the bond they held
        // most thinly and comes back up calmer and smaller. Deterministic: the coldest bond
        // goes first, ties broken by id.
        foreach (var id in world.Registry.Keys.ToList())
        {
            var a = world.Find(id)!.Attributes;
            if (a.Strain < 100) continue;

            var coldest = a.Bond
                .OrderBy(kv => kv.Value)
                .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                .FirstOrDefault();

            a.Strain = 55;
            a.Renown = Attributes.Clamp(a.Renown - 4);

            if (coldest.Key is not null)
            {
                a.Bond.Remove(coldest.Key);
                result.Revealed.Add($"{id} 忘了 {coldest.Key}");
                result.Notes.Add($"{id} 失名了：忘了对 {coldest.Key} 的那点温度（压力回到 55，声望 -4）");
            }
            else
            {
                result.Notes.Add($"{id} 失名了：没有可忘的人，只剩空落（压力回到 55，声望 -4）");
            }
        }

        // 5.7 activities -- a character may call into being something the six kinds cannot
        // express. The world keeps the proposal: a name, the rules as text, and who is
        // invited. It never reads the rules, which is what lets it accept any activity
        // anyone invents instead of only the ones we thought of.
        foreach (var order in accepted.Where(o => o.Activity is not null))
        {
            var draft = order.Activity!;
            if (string.IsNullOrWhiteSpace(draft.Name) || string.IsNullOrWhiteSpace(draft.Rules))
            {
                result.Notes.Add($"{order.ActorId} 想发起一场活动，但没写清名字或规则");
                continue;
            }

            var invited = (draft.Participants ?? Array.Empty<string>())
                .Where(id => !string.IsNullOrWhiteSpace(id) && world.Find(id) is not null)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (invited.Count < 2)
            {
                result.Notes.Add($"{order.ActorId} 想发起「{draft.Name}」，但凑不够两个人");
                continue;
            }

            var activity = new Activity
            {
                Id = "act-" + (world.Activities.Count + 1),
                Name = draft.Name.Trim(),
                HostId = order.ActorId,
                Rules = draft.Rules.Trim()
            };
            activity.Participants.AddRange(invited);
            world.Activities.Add(activity);
            result.Notes.Add($"{order.ActorId} 发起了一场「{activity.Name}」，等参与者落子");
            result.Revealed.Add($"{order.ActorId} 摆开一场「{activity.Name}」");
        }

        // 5.8 naming -- a character settles on what it calls someone. Cheap to do, and it does
        // more for a scene than most numbers: once recorded, the driver prompt carries the
        // nickname, so every later line uses it without ever being told again.
        foreach (var order in accepted.Where(o => !string.IsNullOrWhiteSpace(o.Nickname) && o.TargetId is not null))
        {
            var actor = world.Find(order.ActorId)!;
            var target = world.Find(order.TargetId!);
            if (target is null || target.Id == actor.Id) continue;

            var nickname = order.Nickname!.Trim();
            if (nickname.Length > 16) nickname = nickname[..16];

            actor.Attributes.Names[target.Id] = nickname;
            actor.Attributes.ShiftBond(target.Id, 2);
            target.Attributes.ShiftBond(actor.Id, 1);
            result.Notes.Add($"{actor.Id} 从此管 {target.Id} 叫「{nickname}」");
        }

        // 5.85 impressions -- what one person makes of another. One short line, kept privately:
        // two people looking at the same face write two different lines, and that gap is where
        // most trouble between people comes from. The world never checks them against anything.
        foreach (var order in accepted.Where(o => !string.IsNullOrWhiteSpace(o.Impression) && o.TargetId is not null))
        {
            var actor = world.Find(order.ActorId)!;
            var target = world.Find(order.TargetId!);
            if (target is null || target.Id == actor.Id) continue;

            var impression = order.Impression!.Trim();
            if (impression.Length > 60) impression = impression[..60];

            actor.Attributes.Impressions[target.Id] = impression;
            result.Notes.Add($"{actor.Id} 记住了 {target.Id}：{impression}");
        }

        // 5.9 traits -- what a round does to a person over time. Prickliness does not vanish
        // when honesty grows; it quiets. Nothing is ever replaced here, only added to and
        // turned down, which is the difference between a character who changes and one who
        // simply degrades into a different label.
        foreach (var order in accepted)
        {
            var actor = world.Find(order.ActorId)!;
            switch (order.Kind)
            {
                case OrderKind.Reveal:
                    actor.Traits.Shift("坦率", 8);
                    actor.Traits.Shift("傲娇", -3);
                    break;
                case OrderKind.Withhold:
                    actor.Traits.Shift("内敛", 4);
                    break;
                case OrderKind.Give:
                    actor.Traits.Shift("心软", 4);
                    break;
                case OrderKind.Probe:
                    actor.Traits.Shift("敏锐", 3);
                    break;
            }
        }

        // 5.95 divination -- a reach that aims neither at a person nor at the room but at what
        // has not happened yet. It is also the only order whose value can be sold, so its price
        // is settled here rather than waiting for a venue system that does not exist yet.
        // A tool the character does not hold fails cleanly and is recorded, not silently dropped.
        foreach (var order in accepted.Where(o => o.Kind == OrderKind.Divine))
        {
            var actor = world.Find(order.ActorId)!;
            var tool = order.Tool ?? GuessTool(actor);
            var querent = order.TargetId is null ? null : world.Find(order.TargetId);
            var outcome = Divination.Perform(world, result, actor, tool, querent, order.Text, order.Advice);
            if (!outcome.Ok) result.Notes.Add($"{order.ActorId} 想占卜，但{outcome.Reason}");
        }

        // 5.955 asking -- a request is not a mood, it is a thing said out loud with a shape:
        // what is missing, and what would close the gap. Recording it here is what makes the
        // other two roads possible. Without this step a resident can only wait to be helped and
        // never ask, and the whole three-exit design would have exactly one working exit.
        foreach (var order in accepted.Where(o => o.Kind == OrderKind.Plea))
        {
            var actor = world.Find(order.ActorId)!;
            var blocked = order.Blocked ?? Blocked.Object;
            var needs = string.IsNullOrWhiteSpace(order.Needs)
                ? (string.IsNullOrWhiteSpace(order.Text) ? "说了一件想要的事" : order.Text!)
                : order.Needs!;
            var plea = Pleas.Open(world, actor, blocked, needs, order.Text ?? "", order.TargetId);
            result.Revealed.Add($"{order.ActorId} 开了口：{plea.Needs}");
            result.Notes.Add($"{order.ActorId} 的请求 {plea.Id}（{BlockedText.Of(blocked)}）");
        }

        // 5.96 answering -- a promise, not a transfer. Answering settles the request; whether the
        // thing asked for actually changes hands is a separate give. Both are needed, which is
        // what stops a plea from being solved by words alone.
        foreach (var order in accepted.Where(o => o.Kind == OrderKind.Answer && o.PleaId is not null))
        {
            var actor = world.Find(order.ActorId)!;
            var (pled, reason) = Pleas.Answer(world, order.PleaId!, actor);
            if (pled is null)
            {
                result.Notes.Add($"{order.ActorId} 想应一条请求，但{reason}");
                continue;
            }

            result.Revealed.Add($"{order.ActorId} 应了 {pled.FromId} 的请求");
            result.Notes.Add($"{order.ActorId} 应下「{pled.Needs}」");
        }

        // 6+7. clues and convergence are story-level; the resolver only reports.
        // Convergence is per scene: one group finishing their thread does not end the evening
        // for everyone else, it just means that group's scene is over.
        result.Converged = scene.CluesResolved >= scene.ClueGoal;

        // 8. archive -- every accepted order lands in the history of the scene it happened in
        foreach (var order in accepted)
        {
            scene.Record(order.ActorId, order.Kind.ToString().ToLowerInvariant(),
                order.Text ?? "", order.TargetId);
        }

        // Everything the settlement revealed goes into the history as well. This used to live
        // only in the response body, which meant the most dramatic thing a round can produce --
        // someone being seen through -- was never on stage at all: the audience reads history,
        // not the HTTP response.
        foreach (var line in result.Revealed)
            scene.Record("旁白", "reveal", line, null);

        foreach (var id in world.Registry.Keys)
        {
            var a = world.Find(id)!.Attributes;
            result.Deltas[id] = new AttributeDelta(a.Poise, a.Insight, a.Renown, a.Strain, a.Reserve, a.Ember);
        }

        // 8.5 faces -- read from the settled round here rather than in the stage, so that every
        // way of settling (the loop, the manual endpoint, a test) leaves behind the same
        // expressions. A face decided by the viewer would be a second, disagreeing account of
        // what just happened.
        foreach (var id in scene.Cast.Active)
        {
            if (world.Find(id) is not { } who) continue;
            world.Faces[id] = ExpressionRules.Of(id, round, result, who);
            world.Shots[id] = ShotRules.Of(id, round, result, who);
        }

        round.Settle(result);
        return result;
    }
}