namespace Clarry.Core;

/// <summary>
/// Hard limits of the theatre. These are capacity limits, not registry limits:
/// any number of characters may be registered, but only this many may act at once.
/// </summary>
public static class StageLimits
{
    /// <summary>How many agents may be simultaneously active on stage.</summary>
    public const int MaxActiveAgents = 64;
}

/// <summary>
/// The stage is the set of characters currently able to act. It is a capacity-bounded
/// view over the (unbounded) registry. A character enters when the scene needs them and
/// leaves when they walk off; the turn order only ever iterates the stage.
/// </summary>
public sealed class Stage
{
    private readonly List<string> _active = new();
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

    public IReadOnlyList<string> Active => _active;
    public int Count => _active.Count;
    public int Capacity => StageLimits.MaxActiveAgents;
    public bool IsFull => _active.Count >= StageLimits.MaxActiveAgents;

    public sealed record EnterResult(bool Entered, string? Reason);

    /// <summary>Put a character on stage. Idempotent for one already present.</summary>
    public EnterResult Enter(string characterId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(characterId);
        if (_seen.Contains(characterId)) return new EnterResult(true, "已经在台上");
        if (IsFull)
        {
            // Refusing here is the whole point of the limit: a 65th agent waits its turn
            // instead of silently making every turn slower for the other 64.
            return new EnterResult(false, "台上已经满了");
        }
        _active.Add(characterId);
        _seen.Add(characterId);
        return new EnterResult(true, null);
    }

    /// <summary>Take a character off stage. They stay in the registry.</summary>
    public bool Leave(string characterId)
    {
        if (!_seen.Remove(characterId)) return false;
        _active.Remove(characterId);
        return true;
    }

    /// <summary>Advance the turn pointer by one, wrapping at the end of the stage.</summary>
    public int NextTurn(int current)
    {
        if (_active.Count == 0) return 0;
        return (current + 1) % _active.Count;
    }
}