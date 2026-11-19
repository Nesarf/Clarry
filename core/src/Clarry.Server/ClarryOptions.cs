namespace Clarry.Server;

using Clarry.Core;

/// <summary>
/// Every number that used to be scattered through the code, in one place, read once at startup
/// from the environment. The same binary should be able to be a laptop demo and a sixty-four
/// seat run without a recompile, and the difference between those two is mostly this file.
///
/// The first entry is the one that matters: how many drivers may be in flight at once. Everything
/// else is how long the theatre is willing to wait for an answer it may never get.
/// </summary>
public sealed class ClarryOptions
{
    /// <summary>
    /// How many drivers may be in flight at once. This single number decides whether sixty-four
    /// seats is a theatre or a smoke test: asking every seat at the same moment is what a naive
    /// loop does, and it is also how you find the rate limit of whatever is behind them.
    /// </summary>
    public int MaxConcurrentDrivers { get; init; } = 4;

    /// <summary>How long one driver may take before it is written off for this round.</summary>
    public int DriverTimeoutMs { get; init; } = 180_000;

    /// <summary>
    /// How long all drivers together may take for one scene's round. Past this the remaining
    /// seats are treated as silent: a round that never ends stops the whole theatre, and being
    /// slow is not the same thing as being broken.
    /// </summary>
    public int RoundBudgetMs { get; init; } = 300_000;

    /// <summary>Pause between beats.</summary>
    public int BeatIntervalMs { get; init; } = 1_500;

    /// <summary>How long the theatre waits for a human seat before settling without an answer.</summary>
    public int UserWaitMs { get; init; } = 120_000;

    /// <summary>How many closed scenes to keep as record before the oldest are trimmed.</summary>
    public int ClosedSceneKeep { get; init; } = 30;

    /// <summary>
    /// One round in this many, per person, a glimpse lands. Two thousand is deliberately large:
    /// somebody who plays a hundred rounds still almost certainly never feels it, and that is the
    /// design rather than a rough edge.
    ///
    /// The acceptance suite sets this to 1 through the environment, because waiting for a
    /// one-in-two-thousand coincidence is not a test, it is a hope.
    /// </summary>
    public int GlimpseOdds { get; init; } = GlimpseRules.DefaultOdds;

    public static ClarryOptions FromEnvironment()
    {
        static int Read(string name, int fallback)
        {
            var raw = Environment.GetEnvironmentVariable(name);
            return int.TryParse(raw, out var value) && value > 0 ? value : fallback;
        }

        return new ClarryOptions
        {
            MaxConcurrentDrivers = Read("CLARRY_MAX_CONCURRENT", 4),
            DriverTimeoutMs = Read("CLARRY_DRIVER_TIMEOUT_MS", 180_000),
            RoundBudgetMs = Read("CLARRY_ROUND_BUDGET_MS", 300_000),
            BeatIntervalMs = Read("CLARRY_BEAT_INTERVAL_MS", 1_500),
            UserWaitMs = Read("CLARRY_USER_WAIT_MS", 120_000),
            ClosedSceneKeep = Read("CLARRY_CLOSED_SCENE_KEEP", 30),
            GlimpseOdds = Read("CLARRY_GLIMPSE_ODDS", GlimpseRules.DefaultOdds)
        };
    }

    /// <summary>What this configuration means in one line, for the log and for /api/loop/status.</summary>
    public string Describe() =>
        $"最多 {MaxConcurrentDrivers} 路并发，单路 {DriverTimeoutMs / 1000} 秒，"
        + $"每幕每轮 {RoundBudgetMs / 1000} 秒封顶";
}
