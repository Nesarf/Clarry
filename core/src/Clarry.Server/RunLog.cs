using System.Text.Json;
using System.Text.Json.Serialization;

namespace Clarry.Server;

/// <summary>
/// One beat, as it happened. Not a log line -- a row: every number that costs money or explains
/// silence, in one place.
/// </summary>
public sealed class BeatRecord
{
    public DateTimeOffset At { get; init; } = DateTimeOffset.UtcNow;
    public int Beat { get; init; }
    public int Scenes { get; init; }

    /// <summary>Seats asked this beat, and seats that had no reason to be asked.</summary>
    public int Asked { get; init; }
    public int Quiet { get; init; }

    /// <summary>The high-water mark of drivers in flight during this beat.</summary>
    public int PeakInFlight { get; init; }

    /// <summary>Prompt characters and the tokens the provider says it charged, for this beat.</summary>
    public long PromptChars { get; init; }
    public long PromptTokens { get; init; }
    public long CachedTokens { get; init; }

    /// <summary>Rounds settled this beat, how many converged, how many orders were thrown away.</summary>
    public int Settled { get; init; }
    public int Converged { get; init; }
    public int Ignored { get; init; }

    /// <summary>Requests that lapsed this beat.</summary>
    public int Lapsed { get; init; }

    public long ElapsedMs { get; init; }
}

/// <summary>
/// One line per beat on disk.
///
/// This is not logging, it is a ledger. The loop's in-memory log answers "what just happened";
/// this answers "what did last night look like" -- which is the question you ask the moment
/// sixty-four seats start costing real money, or the moment a beat takes ninety seconds and
/// nobody can say which seat did it.
///
/// An append-only JSONL file, trimmed by rewriting when it grows past its keep count. Small
/// enough that reading the last week is one file read, structured enough to be sorted and summed.
/// </summary>
public sealed class RunLog
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly string _path;
    private readonly int _keep;
    private readonly object _lock = new();

    public RunLog(string path, int keep = 5000)
    {
        _path = path;
        _keep = Math.Max(100, keep);

        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    }

    /// <summary>Where the ledger lives, for the status endpoint to say out loud.</summary>
    public string Location => _path;

    public void Append(BeatRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        lock (_lock)
        {
            try
            {
                File.AppendAllText(_path, JsonSerializer.Serialize(record, Options) + Environment.NewLine);
                TrimIfNeeded();
            }
            catch (IOException)
            {
                // A busy disk is not a reason to stop the theatre. The beat already happened;
                // losing its row is worth less than losing the evening.
            }
        }
    }

    /// <summary>The last <paramref name="take"/> rows, oldest first. A torn last line is skipped.</summary>
    public List<BeatRecord> Read(int take = 50)
    {
        lock (_lock)
        {
            if (!File.Exists(_path)) return new List<BeatRecord>();

            var rows = new List<BeatRecord>();
            foreach (var line in File.ReadLines(_path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var row = JsonSerializer.Deserialize<BeatRecord>(line, Options);
                    if (row is not null) rows.Add(row);
                }
                catch (JsonException)
                {
                    // Half a line from a hard stop. It is not worth failing the whole read for.
                }
            }

            return rows.Count <= take ? rows : rows.GetRange(rows.Count - take, take);
        }
    }

    /// <summary>
    /// Rewrites the file when it has run well past its keep count. Amortised rather than exact:
    /// rewriting on every append would turn a ledger into a performance bug.
    /// </summary>
    private void TrimIfNeeded()
    {
        if (!File.Exists(_path)) return;

        var lines = File.ReadAllLines(_path);
        if (lines.Length <= _keep + (_keep / 4)) return;

        var kept = lines.Skip(lines.Length - _keep).ToArray();
        File.WriteAllLines(_path, kept);
    }
}
