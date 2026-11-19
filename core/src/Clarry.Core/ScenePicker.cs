namespace Clarry.Core;

/// <summary>
/// One proposed scene: who would end up in a room together, and the reason the world gives
/// for it. The reason is not decoration -- the audience is meant to read it.
/// </summary>
public sealed record SceneProposal(IReadOnlyList<string> Cast, string Reason)
{
    public bool IsSolo => Cast.Count == 1;
}

/// <summary>
/// Decides who shares a scene, the way a life-sim decides who bumps into whom: not by lots,
/// but by how warm the two sides already are toward each other.
///
/// The rule is deliberately deterministic. Mutual warmth (both bonds added) sorts the pairs;
/// the warmest pair takes the stage and leaves the pool; ties break on id, so the same world
/// always stages the same scenes. An audience that comes back tomorrow should find the same
/// people sitting together, not a reshuffled cast.
/// </summary>
public static class ScenePicker
{
    /// <summary>How many characters one scene may hold. Above this, nobody gets a line.</summary>
    public const int MaxPerScene = 2;

    public static List<SceneProposal> Propose(World world) =>
        Propose(world, world.Stage.Active);

    /// <summary>
    /// Proposes scenes for an explicit candidate list. Needed once there is more than one scene:
    /// "on stage" stops meaning "everyone", and the caller has to say who is free to be cast.
    /// </summary>
    public static List<SceneProposal> Propose(World world, IEnumerable<string> candidates)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(candidates);

        var seated = candidates
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();
        var proposals = new List<SceneProposal>();
        if (seated.Count == 0) return proposals;

        int Warmth(string a, string b)
        {
            var ca = world.Find(a);
            var cb = world.Find(b);
            if (ca is null || cb is null) return 0;
            return ca.Attributes.BondWith(b) + cb.Attributes.BondWith(a);
        }

        var pairs = new List<(string A, string B, int Warmth)>();
        for (var i = 0; i < seated.Count; i++)
            for (var j = i + 1; j < seated.Count; j++)
                pairs.Add((seated[i], seated[j], Warmth(seated[i], seated[j])));

        pairs.Sort((x, y) =>
        {
            var byWarmth = y.Warmth.CompareTo(x.Warmth);
            if (byWarmth != 0) return byWarmth;
            var byFirst = string.CompareOrdinal(x.A, y.A);
            return byFirst != 0 ? byFirst : string.CompareOrdinal(x.B, y.B);
        });

        var taken = new HashSet<string>(StringComparer.Ordinal);

        // A wish already bought this encounter, so it outranks warmth: the audience paid.
        if (world.ForcedPairA is { } forcedA && world.ForcedPairB is { } forcedB
            && forcedA != forcedB
            && seated.Contains(forcedA) && seated.Contains(forcedB))
        {
            taken.Add(forcedA);
            taken.Add(forcedB);
            proposals.Add(new SceneProposal(
                new[] { forcedA, forcedB },
                $"有人许了愿，让 {forcedA} 与 {forcedB} 见面"));
        }

        foreach (var (a, b, warmth) in pairs)
        {
            if (proposals.Count * MaxPerScene >= seated.Count) break;
            if (taken.Contains(a) || taken.Contains(b)) continue;

            taken.Add(a);
            taken.Add(b);
            proposals.Add(new SceneProposal(
                new[] { a, b },
                warmth > 0
                    ? $"{a} 与 {b} 之间的温度是 {warmth}，凑到一起会有话"
                    : $"{a} 与 {b} 还不认识，先放一起看看"));
        }

        foreach (var id in seated)
        {
            if (taken.Contains(id)) continue;
            proposals.Add(new SceneProposal(new[] { id }, $"{id} 此刻没有可凑的人，一个人待着"));
        }

        return proposals;
    }
}
