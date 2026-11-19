namespace Clarry.Server;

using Clarry.Core;

/// <summary>
/// Everything the endpoints share: the world, the stores, the gate, the loop, the ledger.
///
/// These used to be a dozen top-level variables in Program.cs, captured by closures. That meant
/// every endpoint could reach everything, and moving one into its own file meant rewriting its
/// body. Passing this instead makes the dependency visible in the signature and leaves the body
/// alone -- which is the only way a two thousand line file gets smaller without changing shape.
/// </summary>
public sealed class ClarryApp
{
    public required World World { get; init; }
    public required WorldStore Store { get; init; }
    public required MemoryStore Memories { get; init; }

    /// <summary>One gate for every mutation: a loop tick and a manual call take turns.</summary>
    public required SemaphoreSlim Gate { get; init; }

    public required RoundLoop Loop { get; init; }
    public required RunLog Runs { get; init; }
    public required ClarryOptions Options { get; init; }
    public required string StagePage { get; init; }

    /// <summary>True when the world came off disk rather than being made fresh. Shown by /api/health.</summary>
    public required bool Loaded { get; init; }

    public void Persist() => Store.Save(World);
}
