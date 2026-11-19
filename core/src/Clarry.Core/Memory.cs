using Microsoft.Data.Sqlite;

namespace Clarry.Core;

/// <summary>
/// How one character remembers, derived from who they are. Personality is not decoration here:
/// two people at the same event should come away with different amounts of it.
/// </summary>
public sealed class MemoryStyle
{
    /// <summary>Multiplier applied each round. Higher means the memory lasts longer.</summary>
    public double Decay { get; init; } = 0.92;

    /// <summary>Weight bonus for everything this character witnesses.</summary>
    public double Salience { get; init; } = 1.0;

    /// <summary>How many memories survive at all.</summary>
    public int Capacity { get; init; } = 40;

    /// <summary>How many are pulled into the prompt each round.</summary>
    public int Recall { get; init; } = 6;

    /// <summary>
    /// Reads the traits a character already has. Chinese trait words are matched rather than
    /// exact-compared because the sheets are written by hand and by models, so they vary.
    /// </summary>
    public static MemoryStyle For(Character character)
    {
        ArgumentNullException.ThrowIfNull(character);

        var decay = 0.92;
        var salience = 1.0;
        var capacity = 40;
        var recall = 6;

        foreach (var trait in character.Sheet.Traits)
        {
            if (trait.Contains("谨慎") || trait.Contains("好奇") || trait.Contains("迂腐"))
            {
                decay += 0.02;
                capacity += 10;
            }
            if (trait.Contains("健忘") || trait.Contains("粗心"))
            {
                decay -= 0.06;
                capacity -= 12;
            }
            if (trait.Contains("敏感") || trait.Contains("敏锐"))
            {
                salience += 0.30;
                recall += 2;
            }
            if (trait.Contains("直率"))
            {
                capacity -= 6;
            }
            if (trait.Contains("心软") || trait.Contains("护短"))
            {
                salience += 0.15;
            }
        }

        return new MemoryStyle
        {
            Decay = Math.Clamp(decay, 0.60, 0.99),
            Salience = Math.Clamp(salience, 0.5, 3.0),
            Capacity = Math.Clamp(capacity, 10, 200),
            Recall = Math.Clamp(recall, 2, 16)
        };
    }
}

/// <summary>One thing a character remembers. Belongs to that character, not to the world.</summary>
public sealed record Memory(
    long Id,
    string AgentId,
    string Text,
    string? AboutId,
    double Weight,
    DateTimeOffset At);

/// <summary>
/// Memory in SQLite rather than one growing text file.
///
/// The questions asked of it are "the six heaviest memories about that person" and "what has
/// decayed past saving", not "what does the whole file say". In a file those are a full parse
/// plus hand-written filtering; here they are an indexed query. Sixty-four characters, dozens
/// of memories each, hundreds of rounds: the file becomes something nobody dares touch.
///
/// SQLite is the right size for that: embedded, no service, one file but with indexes and
/// transactions.
/// </summary>
public sealed class MemoryStore : IDisposable
{
    private readonly SqliteConnection _connection;

    /// <summary>Weight multipliers by how much the act itself was worth remembering.</summary>
    private static readonly Dictionary<string, double> KindWeight = new(StringComparer.OrdinalIgnoreCase)
    {
        ["reveal"] = 90,
        ["probe"] = 75,
        ["give"] = 70,
        ["act"] = 45,
        ["activity"] = 60,
        ["speak"] = 30,
        ["withhold"] = 20,
        ["divine"] = 55,
        ["game"] = 35
    };

    public MemoryStore(string path)
    {
        // Fully qualified because this class exposes a property also named Path, which shadows
        // System.IO.Path inside the class body.
        var dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        _connection = new SqliteConnection($"Data Source={path}");
        _connection.Open();

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS memories (
                id       INTEGER PRIMARY KEY AUTOINCREMENT,
                agent_id TEXT    NOT NULL,
                text     TEXT    NOT NULL,
                about_id TEXT,
                weight   REAL    NOT NULL,
                at       TEXT    NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_memories_agent
                ON memories (agent_id, weight DESC, at DESC);
            """;
        cmd.ExecuteNonQuery();
    }

    public string Path => _connection.DataSource;

    public static double WeightOf(string kind) =>
        KindWeight.TryGetValue(kind, out var w) ? w : 25;

    /// <summary>Writes one memory. Weight is the act's base value, lifted by how sensitive they are.</summary>
    public void Remember(string agentId, string text, string? aboutId, double weight,
                         MemoryStyle style, DateTimeOffset at)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO memories (agent_id, text, about_id, weight, at)
            VALUES ($a, $t, $b, $w, $at);
            """;
        cmd.Parameters.AddWithValue("$a", agentId);
        cmd.Parameters.AddWithValue("$t", text.Length > 400 ? text[..400] : text);
        cmd.Parameters.AddWithValue("$b", (object?)aboutId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$w", Math.Round(weight * style.Salience, 2));
        cmd.Parameters.AddWithValue("$at", at.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// The heaviest memories, newest first among equals. Deterministic: same rows, same order,
    /// every time -- which is what keeps a replayed world replaying the same.
    /// </summary>
    public List<Memory> Recall(string agentId, int limit)
    {
        var found = new List<Memory>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, agent_id, text, about_id, weight, at
            FROM memories
            WHERE agent_id = $a
            ORDER BY weight DESC, at DESC, id DESC
            LIMIT $n;
            """;
        cmd.Parameters.AddWithValue("$a", agentId);
        cmd.Parameters.AddWithValue("$n", Math.Max(1, limit));

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            found.Add(new Memory(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetDouble(4),
                DateTimeOffset.TryParse(reader.GetString(5), out var at) ? at : DateTimeOffset.UtcNow));
        }
        return found;
    }

    /// <summary>Fades everything, drops what fell below the floor, then trims to capacity.</summary>
    public void DecayAndTrim(string agentId, MemoryStyle style)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            UPDATE memories SET weight = weight * $d WHERE agent_id = $a;
            DELETE FROM memories WHERE agent_id = $a AND weight < 5;
            DELETE FROM memories WHERE agent_id = $a AND id NOT IN (
                SELECT id FROM memories WHERE agent_id = $a ORDER BY weight DESC, at DESC, id DESC LIMIT $cap
            );
            """;
        cmd.Parameters.AddWithValue("$a", agentId);
        cmd.Parameters.AddWithValue("$d", style.Decay);
        cmd.Parameters.AddWithValue("$cap", style.Capacity);
        cmd.ExecuteNonQuery();
    }

    public int Count(string agentId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM memories WHERE agent_id = $a;";
        cmd.Parameters.AddWithValue("$a", agentId);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>Everyone who has ever remembered anything -- used to decay them all each round.</summary>
    public List<string> Agents()
    {
        var found = new List<string>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT agent_id FROM memories;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) found.Add(reader.GetString(0));
        return found;
    }

    /// <summary>Wipes one character's memory, or the whole store when no id is given.</summary>
    public int Clear(string? agentId = null)
    {
        using var cmd = _connection.CreateCommand();
        if (string.IsNullOrWhiteSpace(agentId))
        {
            cmd.CommandText = "DELETE FROM memories;";
        }
        else
        {
            cmd.CommandText = "DELETE FROM memories WHERE agent_id = $a;";
            cmd.Parameters.AddWithValue("$a", agentId);
        }
        return cmd.ExecuteNonQuery();
    }

    public void Dispose() => _connection.Dispose();
}

/// <summary>
/// Turns a settled round into memories.
///
/// Kept outside <see cref="RoundResolver"/> deliberately: the resolver is pure bookkeeping, and
/// reaching into a database from inside it would make a settlement impossible to test without
/// one. Both the loop and the manual resolve endpoint call this, so memory does not depend on
/// who advanced the round.
/// </summary>
public static class MemoryRecorder
{
    public static void Record(Round round, World world, MemoryStore memories, DateTimeOffset? at = null)
    {
        ArgumentNullException.ThrowIfNull(round);
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(memories);

        var now = at ?? DateTimeOffset.UtcNow;

        foreach (var order in round.Orders)
        {
            var kind = order.Kind.ToString().ToLowerInvariant();
            var label = OrderKindText.Of(order.Kind);
            var baseWeight = MemoryStore.WeightOf(kind);
            var text = string.IsNullOrWhiteSpace(order.Text) ? label : order.Text!;

            if (world.Find(order.ActorId) is { } actor)
            {
                memories.Remember(order.ActorId, $"{label}：{text}", order.TargetId,
                                  baseWeight, MemoryStyle.For(actor), now);
            }

            if (order.TargetId is null) continue;
            if (world.Find(order.TargetId) is not { } target) continue;

            // Being spoken to is something that happens to you; doing it is something you carry.
            memories.Remember(order.TargetId, $"{order.ActorId} 来{label}：{text}", order.ActorId,
                              baseWeight * 0.9, MemoryStyle.For(target), now);
        }

        // Everything fades a little each round, whether or not it was touched this time.
        foreach (var id in memories.Agents())
        {
            if (world.Find(id) is not { } c) continue;
            memories.DecayAndTrim(id, MemoryStyle.For(c));
        }
    }
}
