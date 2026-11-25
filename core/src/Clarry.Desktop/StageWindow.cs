using System.Diagnostics;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Clarry.Desktop;

/// <summary>
/// The window itself: one WebView2 filling the frame, a menu over it, a line of state under it.
///
/// The stage page is served by ClarryHost and painted by the same HTML the browser stage uses,
/// so a fix to the stage lands in both places at once. The window adds only what a browser tab
/// cannot: it can start the world, and closing it says so.
/// </summary>
internal sealed class StageWindow : Form
{
    private static readonly Color Ink = Color.FromArgb(0x1A, 0x1E, 0x3A);
    private static readonly Color Parchment = Color.FromArgb(0xF5, 0xEB, 0xDC);
    private static readonly Color Night = Color.FromArgb(0x2B, 0x1B, 0x4A);
    private static readonly Color Gilt = Color.FromArgb(0xA8, 0x89, 0x4A);

    private readonly string _url;
    private readonly string _root;
    private readonly bool _hosted;
    private readonly WebView2 _view = new() { Dock = DockStyle.Fill };
    private readonly ToolStripStatusLabel _status = new() { Text = "正在打开舞台…" };

    internal StageWindow(string url, string root, bool hosted)
    {
        _url = url;
        _root = root;
        _hosted = hosted;

        Text = "Clarry";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(880, 560);
        Size = new Size(1440, 900);
        BackColor = Ink;
        KeyPreview = true;

        // Fill first, edges after: docking lays out from the last child added, so a fill added
        // last would sit under the menu and the state line.
        Controls.Add(_view);
        Controls.Add(BuildState());
        Controls.Add(BuildMenu());
    }

    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        await AttachAsync();
    }

    private async Task AttachAsync()
    {
        try
        {
            // The browser profile lives under data/ with the world: gitignored, next to the
            // thing it belongs to, and never in the user's own profile directory.
            var dataDir = Environment.GetEnvironmentVariable("CLARRY_DATA") ?? Path.Combine(_root, "data");
            var profile = Path.Combine(dataDir, "webview");
            Directory.CreateDirectory(profile);

            var env = await CoreWebView2Environment.CreateAsync(browserExecutableFolder: null, userDataFolder: profile);
            await _view.EnsureCoreWebView2Async(env);

            _view.CoreWebView2.Settings.IsStatusBarEnabled = false;
            _view.CoreWebView2.Settings.AreDevToolsEnabled = true;
            _view.CoreWebView2.Navigate(_url + "/stage");

            _status.Text = _hosted
                ? $"本窗口内运行 · {_url}"
                : $"连接到已经在跑的世界 · {_url}";
            _status.ForeColor = Parchment;
        }
        catch (Exception ex)
        {
            ShowFallback(ex);
        }
    }

    private StatusStrip BuildState()
    {
        var strip = new StatusStrip
        {
            BackColor = Night,
            SizingGrip = true,
        };
        _status.ForeColor = Gilt;
        strip.Items.Add(_status);
        strip.Items.Add(new ToolStripStatusLabel { Spring = true, Text = string.Empty });
        return strip;
    }

    private MenuStrip BuildMenu()
    {
        var menu = new MenuStrip
        {
            Dock = DockStyle.Top,
            BackColor = Night,
            ForeColor = Parchment,
        };

        var stage = new ToolStripMenuItem("舞台");
        stage.DropDownItems.Add(Item("刷新", Keys.F5, (_, _) => Reload()));
        stage.DropDownItems.Add(Item("在浏览器中打开", Keys.Control | Keys.B, (_, _) => OpenInBrowser()));
        stage.DropDownItems.Add(Item("复制舞台地址", Keys.None, (_, _) => CopyAddress()));

        var view = new ToolStripMenuItem("视图");
        view.DropDownItems.Add(Item("放大", Keys.Control | Keys.Oemplus, (_, _) => Zoom(1.1)));
        view.DropDownItems.Add(Item("缩小", Keys.Control | Keys.OemMinus, (_, _) => Zoom(1 / 1.1)));
        view.DropDownItems.Add(Item("原始大小", Keys.Control | Keys.D0, (_, _) => ResetZoom()));

        var help = new ToolStripMenuItem("帮助");
        help.DropDownItems.Add(Item("打开工程目录", Keys.None, (_, _) => OpenFolder()));
        help.DropDownItems.Add(Item("关于这个窗口", Keys.None, (_, _) => ShowAbout()));

        menu.Items.AddRange([stage, view, help]);
        return menu;
    }

    private static ToolStripMenuItem Item(string text, Keys keys, EventHandler onClick)
    {
        var item = new ToolStripMenuItem(text);
        if (keys != Keys.None) item.ShortcutKeys = keys;
        item.Click += onClick;
        return item;
    }

    private void Reload()
    {
        if (_view.CoreWebView2 is not null) _view.CoreWebView2.Reload();
        else _view.Source = new Uri(_url + "/stage");
    }

    private void OpenInBrowser() =>
        Process.Start(new ProcessStartInfo(_url + "/stage") { UseShellExecute = true });

    private void CopyAddress()
    {
        try { Clipboard.SetText(_url + "/stage"); _status.Text = "地址已复制 · " + _url; }
        catch { _status.Text = "复制地址没成功 · " + _url; }
    }

    private void Zoom(double factor)
    {
        if (_view.CoreWebView2 is null) return;
        _view.ZoomFactor = Math.Clamp(_view.ZoomFactor * factor, 0.5, 3.0);
        _status.Text = $"缩放 {_view.ZoomFactor:0.00}× · {_url}";
    }

    private void ResetZoom()
    {
        if (_view.CoreWebView2 is null) return;
        _view.ZoomFactor = 1.0;
        _status.Text = _hosted ? $"本窗口内运行 · {_url}" : $"连接到已经在跑的世界 · {_url}";
    }

    private void OpenFolder() =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_root}\"") { UseShellExecute = true });

    private void ShowAbout() => MessageBox.Show(
        $"工程根：{_root}\n舞台地址：{_url}/stage\n\n"
        + (_hosted
            ? "世界由这个窗口自己托管：关掉窗口，服务一起停。"
            : "世界已经在别处跑着：关掉窗口不影响它。")
        + "\n\n用 --urls http://127.0.0.1:端口 可以换一个地址启动。",
        "Clarry", MessageBoxButtons.OK, MessageBoxIcon.Information);

    /// <summary>
    /// No WebView2 runtime (or no permission to start one) leaves the address still serving.
    /// Saying that plainly, with a button that opens the browser, is more useful than an empty
    /// dark rectangle that looks like a bug in the theatre.
    /// </summary>
    private void ShowFallback(Exception ex)
    {
        _status.Text = "WebView2 没起来，舞台没能在这里打开";
        _status.ForeColor = Gilt;

        var panel = new Panel { Dock = DockStyle.Fill, BackColor = Ink, Padding = new Padding(28, 24, 28, 20) };
        var open = new Button
        {
            Text = "用浏览器打开舞台",
            Dock = DockStyle.Bottom,
            Height = 38,
            FlatStyle = FlatStyle.Flat,
            BackColor = Night,
            ForeColor = Parchment,
        };
        open.FlatAppearance.BorderColor = Gilt;
        open.Click += (_, _) => OpenInBrowser();

        var text = new Label
        {
            Dock = DockStyle.Fill,
            ForeColor = Parchment,
            Text = "这个窗口靠 WebView2 显示舞台，它在这台机器上没能起来。\n\n"
                 + $"世界本身还在跑：{_url}/stage\n"
                 + "点下面的按钮，用默认浏览器打开同一个舞台。\n\n"
                 + $"技术细节：{ex.GetType().Name}：{ex.Message}",
        };

        panel.Controls.Add(text);
        panel.Controls.Add(open);
        Controls.Add(panel);
        panel.BringToFront();
    }
}
