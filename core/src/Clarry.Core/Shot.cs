namespace Clarry.Core;

/// <summary>
/// How a character is being looked at right now. The wide shot is the stage: everyone in one
/// frame, pixel art, the default. The other two are the theatre leaning in.
///
/// A close-up is for the moment something is said out loud or someone is seen through -- the
/// beat the audience would lean forward for. A memory shot is for a name being lost: the one
/// thing in this world that cannot be shown happening, only shown remembered.
///
/// These are drawn as fine-line illustrations rather than pixel art on purpose. A pixel face
/// cannot hold a close-up; at four hundred percent the grid is the subject, and the expression
/// the point.
/// </summary>
public enum Shot
{
    /// <summary>The stage. Everyone visible, pixel art, no leaning in.</summary>
    Wide,

    /// <summary>One face, fine line, for the beat worth leaning in on.</summary>
    CloseUp,

    /// <summary>A remembered frame. Fine line, quieter: what is lost is shown as recalled.</summary>
    Memory
}

public static class ShotRules
{
    public static string Text(Shot shot) => shot switch
    {
        Shot.Wide => "全场",
        Shot.CloseUp => "特写",
        Shot.Memory => "回忆",
        _ => shot.ToString()
    };

    /// <summary>
    /// Which shot a character is in this round. Memory outranks close-up: losing a name is the
    /// rarer thing, and it is the only one of the two that changes how the picture is treated
    /// rather than only how close it is.
    /// </summary>
    public static Shot Of(string id, Round round, RoundResult result, Character who)
    {
        ArgumentNullException.ThrowIfNull(round);
        ArgumentNullException.ThrowIfNull(result);

        // Hollowing writes "<id> 忘了 <someone>" into the reveal log, and that is the one line
        // in the whole theatre that deserves to be remembered rather than watched.
        if (result.Revealed.Any(line => line.StartsWith(id + " 忘了 ", StringComparison.Ordinal)))
            return Shot.Memory;

        // Saying a true thing out loud, or being the one who saw through somebody.
        if (result.Revealed.Any(line => line.StartsWith(id + " 说了实话", StringComparison.Ordinal)))
            return Shot.CloseUp;
        if (result.Revealed.Any(line => line.StartsWith(id + " 看穿了 ", StringComparison.Ordinal)))
            return Shot.CloseUp;

        // Or being the target of either.
        if (result.Revealed.Any(line => ExpressionRules.PointedAt(line, id)))
            return Shot.CloseUp;

        return Shot.Wide;
    }

    /// <summary>
    /// The image files to try, most specific first. The stage walks the list and stops at the
    /// first one that loads, so an undrawn close-up quietly falls back to the pixel portrait
    /// instead of leaving a hole where a character should be.
    ///
    /// The order is the whole contract: shot-specific, line-art by expression, pixel by
    /// expression, then the one portrait every character is guaranteed to have.
    /// </summary>
    public static List<string> Candidates(string id, Expression expression, Shot shot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        var files = new List<string>();
        var ex = expression.ToString().ToLowerInvariant();
        var shotName = shot.ToString().ToLowerInvariant();

        if (shot != Shot.Wide)
        {
            files.Add($"/assets/portraits/{id}-{shotName}.png");
            files.Add($"/assets/portraits/{id}-line-{ex}.png");
        }

        if (expression != Expression.Neutral)
            files.Add($"/assets/portraits/{id}-{ex}.png");

        files.Add($"/assets/portraits/{id}.png");
        return files;
    }
}
