namespace Clarry.Core;

/// <summary>
/// How strongly each trait currently shows.
///
/// A character is not a fixed label. The one who starts prickly can come out of a story
/// prickly *and* honest -- the prickliness quiets, it does not vanish. That distinction is the
/// whole point: a personality that can only be declared once is a costume, not a person.
///
/// It is also what keeps an agent alive across a long story. A model told "you are tsundere"
/// plays a costume for two hundred rounds; a model told "you are 52 parts prickly and 31 parts
/// honest, and the second one is gaining" is playing a change, which is a different job.
/// </summary>
public sealed class TraitMeter
{
    /// <summary>Where a trait starts when it is first seeded from the character sheet.</summary>
    public const int Seed = 60;

    /// <summary>Below this a trait is still there, but no longer worth announcing.</summary>
    public const int Whisper = 25;

    public Dictionary<string, int> Weights { get; set; } = new(StringComparer.Ordinal);

    public bool IsEmpty => Weights.Count == 0;

    /// <summary>Copies the sheet's declared traits in at full strength. Idempotent.</summary>
    public void FromSheet(IEnumerable<string> names)
    {
        if (names is null) return;

        foreach (var name in names)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            var key = name.Trim();
            if (!Weights.ContainsKey(key)) Weights[key] = Seed;
        }
    }

    /// <summary>
    /// Moves one trait. A trait that does not exist yet is created at zero and then moved, so
    /// something genuinely new can appear -- otherwise a character could only ever fade.
    /// </summary>
    public void Shift(string name, int delta)
    {
        if (string.IsNullOrWhiteSpace(name) || delta == 0) return;

        var key = name.Trim();
        Weights.TryGetValue(key, out var current);
        Weights[key] = Math.Clamp(current + delta, 0, 100);
    }

    /// <summary>The traits worth showing, loudest first. Ties break on name so it stays readable.</summary>
    public List<(string Name, int Weight)> Loudest(int take)
    {
        return Weights
            .Where(kv => kv.Value >= Whisper)
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Take(Math.Max(1, take))
            .Select(kv => (kv.Key, kv.Value))
            .ToList();
    }

    /// <summary>One line describing where this person is right now.</summary>
    public string Describe(int take = 4)
    {
        var loud = Loudest(take);
        return loud.Count == 0
            ? ""
            : string.Join("、", loud.Select(t => $"{t.Name} {t.Weight}"));
    }
}
