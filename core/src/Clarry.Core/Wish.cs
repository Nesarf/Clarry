namespace Clarry.Core;

/// <summary>
/// The few things an audience may ask of the world. Deliberately a closed list: a wish that
/// can be phrased any way at all cannot be implemented, tested, or reproduced, and a wish
/// that costs nothing turns watching into writing.
/// </summary>
public enum WishKind
{
    /// <summary>Stage an encounter the warmth order would not have produced on its own.</summary>
    BringTogether,
    /// <summary>Light an ember in someone, exactly as saying a true thing would.</summary>
    Kindle,
    /// <summary>Take one bond away from someone, exactly as one hollowing would.</summary>
    Forget,
    /// <summary>Point the camera at one scene, and keep it there until told otherwise.</summary>
    Focus,
    /// <summary>End the current scene here.</summary>
    Halt
}

/// <summary>One granted wish, kept so the audience can see what it actually changed.</summary>
public sealed record Wish(WishKind Kind, string? SubjectId, string? TargetId, string Note, DateTimeOffset At)
{
    public static Wish Bring(string a, string b) =>
        new(WishKind.BringTogether, a, b, $"让 {a} 与 {b} 在下一幕遇上", DateTimeOffset.UtcNow);

    public static Wish Kindle(string id) =>
        new(WishKind.Kindle, id, null, $"{id} 得到一点余火", DateTimeOffset.UtcNow);

    public static Wish Forget(string id, string? lost) =>
        new(WishKind.Forget, id, lost,
            lost is null ? $"{id} 没有什么可忘的" : $"{id} 忘掉了 {lost}",
            DateTimeOffset.UtcNow);

    public static Wish Halt() =>
        new(WishKind.Halt, null, null, "这一幕停在这里", DateTimeOffset.UtcNow);

    /// <summary>
    /// The one wish that changes nothing about the people in it. Watching a story is not only
    /// about what happens; it is also about which of the things happening you get to see.
    /// </summary>
    public static Wish Focus(string sceneId) =>
        new(WishKind.Focus, sceneId, null, $"镜头挪到 {sceneId}", DateTimeOffset.UtcNow);
}
