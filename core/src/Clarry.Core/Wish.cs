namespace Clarry.Core;

/// <summary>
/// The few things an audience may ask of the world. Deliberately a closed list of three, and
/// deliberately not the list it used to be.
///
/// It used to hold four ways to arrange what happens: bring two people together, light an ember,
/// take a bond away, stop the scene. Three of those are things a resident can already do by
/// itself -- the scene picker finds people, Reveal lights an ember, hollowing removes a bond --
/// so a hand reaching in to do them was taking a decision away from somebody with every right
/// to make it. That was fine when the residents were furniture. They are not any more.
///
/// What is left is what only an audience can do: point the camera, answer a request that is
/// already waiting, or stop watching. The first two are favours. The third is a right.
/// </summary>
public enum WishKind
{
    /// <summary>Point the camera at one scene and keep it there.</summary>
    Focus,

    /// <summary>
    /// Answer one request that is already open. The only way an audience can change what
    /// happens, and it always goes through a shrine: the world records a god answering, never
    /// a viewer. Nothing here knows what a viewer is.
    /// </summary>
    Grant,

    /// <summary>Stop the play here.</summary>
    Halt
}

/// <summary>One granted wish, kept so the audience can see what it actually changed.</summary>
public sealed record Wish(WishKind Kind, string? SubjectId, string? TargetId, string Note, DateTimeOffset At)
{
    public static Wish Focus(string sceneId) =>
        new(WishKind.Focus, sceneId, null, $"镜头挪到 {sceneId}", DateTimeOffset.UtcNow);

    public static Wish Granted(string pleaId, string forWhom, string deityName) =>
        new(WishKind.Grant, forWhom, pleaId, $"{deityName}应了 {forWhom} 的请求", DateTimeOffset.UtcNow);

    public static Wish Halt() =>
        new(WishKind.Halt, null, null, "这一幕停在这里", DateTimeOffset.UtcNow);
}
