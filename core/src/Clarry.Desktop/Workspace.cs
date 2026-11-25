using System.Net.Http;

namespace Clarry.Desktop;

/// <summary>
/// The three facts the window needs about its surroundings: where the theatre lives on disk,
/// which address to open, and whether somebody is already answering there.
///
/// All three are guesses that can be checked, and each one is checked before it is used. Guessing
/// a root and being wrong shows an empty stage; guessing an address and being wrong starts a
/// second host on a busy port.
/// </summary>
internal static class Workspace
{
    internal const string DefaultUrl = "http://127.0.0.1:3090";

    /// <summary>
    /// The repository root, found by the one thing that only exists there: stage/index.html.
    /// CLARRY_ROOT wins when it is set, then the exe's own directory and its ancestors, then the
    /// current directory and its ancestors.
    /// </summary>
    internal static string? FindRoot()
    {
        var declared = Environment.GetEnvironmentVariable("CLARRY_ROOT");
        if (LooksLikeRoot(declared)) return Path.GetFullPath(declared!);

        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                if (LooksLikeRoot(dir.FullName)) return dir.FullName;
            }
        }

        return null;
    }

    private static bool LooksLikeRoot(string? path) =>
        !string.IsNullOrWhiteSpace(path) && File.Exists(Path.Combine(path, "stage", "index.html"));

    /// <summary>
    /// Which address to open. Accepts the same two spellings the host does, so a shortcut can
    /// carry a different port without a rebuild.
    /// </summary>
    internal static string PickUrl(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.Equals("--urls", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                return Normalize(args[i + 1]);
            if (arg.StartsWith("--urls=", StringComparison.OrdinalIgnoreCase))
                return Normalize(arg["--urls=".Length..]);
            if (arg.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || arg.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return Normalize(arg);
        }

        return DefaultUrl;
    }

    private static string Normalize(string url)
    {
        var trimmed = url.Trim().TrimEnd('/');
        return trimmed.Length == 0 ? DefaultUrl : trimmed;
    }

    /// <summary>
    /// Is a Clarry already answering? One and a half seconds is the whole budget: this runs
    /// before the window appears, and a slow answer is the same thing as no answer here.
    /// </summary>
    internal static bool IsAlive(string url)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(1500) };
            using var response = http.GetAsync(url + "/api/health").GetAwaiter().GetResult();
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// A WinExe has no console, so the server's one-line-per-request logging would go nowhere.
    /// It goes into data/desktop.log instead -- next to the world, ignored by git, and the first
    /// place to look when the window is up but nothing is happening.
    /// </summary>
    internal static void RedirectConsoleTo(string root)
    {
        try
        {
            var dir = Environment.GetEnvironmentVariable("CLARRY_DATA") ?? Path.Combine(root, "data");
            Directory.CreateDirectory(dir);
            var writer = new StreamWriter(
                new FileStream(Path.Combine(dir, "desktop.log"), FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            {
                AutoFlush = true
            };
            Console.SetOut(writer);
            Console.SetError(writer);
            Console.WriteLine($"--- Clarry.Desktop {DateTime.Now:yyyy-MM-dd HH:mm:ss} ---");
        }
        catch
        {
            // A window with no log beats no window.
        }
    }
}
