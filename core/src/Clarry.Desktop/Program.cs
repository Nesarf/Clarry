using Clarry.Server;
using Microsoft.AspNetCore.Builder;

namespace Clarry.Desktop;

/// <summary>
/// Clarry as a program rather than as a page on somebody else's page.
///
/// Two jobs, in this order: make sure a world is being served, then put a window on it. The
/// window is not a second world. When nothing is listening it starts <see cref="ClarryHost"/> in
/// this process; when something already is (a console server left running, another window) it
/// attaches to that one instead. Two hosts on one port is the failure this ordering avoids, and
/// attaching is why closing this window does not kill a world it did not start.
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        var root = Workspace.FindRoot();
        if (root is null)
        {
            MessageBox.Show(
                "找不到 Clarry 的工程根：stage/index.html 不在这里，也不在任何一级上级目录里。\n\n"
                + "先把 CLARRY_ROOT 指向工程根再启动，例如：\n"
                + "    setx CLARRY_ROOT D:\\Clarry",
                "Clarry", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return 1;
        }

        // The host reads this too, so the window and the server always agree on which tree is
        // the theatre. Setting it here rather than in a script is what makes the exe portable.
        Environment.SetEnvironmentVariable("CLARRY_ROOT", root);
        Workspace.RedirectConsoleTo(root);

        var url = Workspace.PickUrl(args);
        WebApplication? owned = null;
        if (!Workspace.IsAlive(url))
        {
            owned = ClarryHost.Build(["--urls", url]);
            owned.StartAsync().GetAwaiter().GetResult();
        }

        try
        {
            using var window = new StageWindow(url, root, owned is not null);
            Application.Run(window);
        }
        finally
        {
            if (owned is not null)
            {
                using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                owned.StopAsync(budget.Token).GetAwaiter().GetResult();
                owned.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }

        return 0;
    }
}
