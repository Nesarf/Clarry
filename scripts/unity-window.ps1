# unity-window.ps1 - 从外面读一个正在运行的窗口，存成 PNG。
#
# 两条路，默认先走第一条：
#
#   1. 直接读（PrintWindow + PW_RENDERFULLCONTENT）。让窗口自己把客户区画到一个内存位图上：
#      不需要它在最前面，也不需要它在屏幕上。被别的窗口盖住、放到第二块屏、拖到屏幕外都读得到。
#      这是默认路径。
#   2. 屏幕拷贝（CopyFromScreen）。从 Windows 合成到屏幕上的像素里裁一块，窗口必须在屏幕上
#      且没被盖住。只在直接读回全黑时退到这里，或者用 -Screen 强制。
#
# 两条路默认都不抢前台。读一个窗口不该改变用户眼前是什么，也不该让被测的东西跑到最前面来
# 证明自己在跑 —— 那会让「读得到」这件事依赖被测窗口获得了焦点。只有 -Focus 才叫到最前，
# 而且那是调试用的：确认抓到的与用户看到的是同一份。
#
# 最小化是唯一的例外：最小化的窗口没有可读的客户区，脚本会把它还原（还原不等于抢前台，
# 它仍然留在原来那一层），还原之后仍然走直接读。
#
# 用法：
#   & scripts\unity-window.ps1                                 抓 Unity 编辑器（直接读）
#   & scripts\unity-window.ps1 -ProcessName Clarry.Desktop      抓 Clarry 的窗口
#   & scripts\unity-window.ps1 -View GameView -Out D:\x.png     只抓某个子窗口
#   & scripts\unity-window.ps1 -Screen                          强制走屏幕拷贝，用来做对照
#   & scripts\unity-window.ps1 -Focus                           顺便把窗口叫到最前（默认不叫）
#
# 输出一行 JSON：路径、尺寸、平均亮度、走的是哪条路。亮度接近 0 就是黑图。

param(
    [string]$Out = (Join-Path (Split-Path -Parent $PSScriptRoot) 'shots/unity.png'),
    [int]$ProcessId = 0,
    [string]$ProcessName = 'Unity',
    [string]$View = '',
    [switch]$Screen,
    [switch]$Focus
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

if (-not ('UnityWin' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public class UnityWin {
    public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr p, EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();

    /// 让窗口把自己画进给定的 DC。PW_RENDERFULLCONTENT = 2，Windows 8.1 起才有，
    /// 它要的正是「把 DWM 合成的那一份也画进来」——少了它，加速渲染的窗口会画成空白。
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);

    static string Title(IntPtr h) { var t = new StringBuilder(300); GetWindowTextW(h, t, 300); return t.ToString(); }

    /// 进程的顶层窗口。Unity 的顶层是个很小的容器，真正的编辑区是它的子窗口。
    public static IntPtr Top(uint pid) {
        IntPtr found = IntPtr.Zero;
        EnumWindows(delegate(IntPtr h, IntPtr l) {
            uint p; GetWindowThreadProcessId(h, out p);
            if (p == pid) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// 名字里带 titlePart 的最大子窗口。Game 与 Scene 停靠在同一个位置，
    /// 同一时刻只有当前激活的那个有窗口句柄，所以找不到就是没激活。
    public static IntPtr Child(uint pid, string titlePart) {
        IntPtr best = IntPtr.Zero;
        long bestArea = 0;
        EnumWindows(delegate(IntPtr h, IntPtr l) {
            uint p; GetWindowThreadProcessId(h, out p);
            if (p != pid) return true;
            EnumChildWindows(h, delegate(IntPtr ch, IntPtr l2) {
                if (Title(ch).IndexOf(titlePart, StringComparison.OrdinalIgnoreCase) < 0) return true;
                RECT r; GetWindowRect(ch, out r);
                long area = (long)(r.Right - r.Left) * (r.Bottom - r.Top);
                if (area > bestArea) { bestArea = area; best = ch; }
                return true;
            }, IntPtr.Zero);
            return true;
        }, IntPtr.Zero);
        return best;
    }

    public static string[] Names(uint pid) {
        var rows = new System.Collections.Generic.List<string>();
        EnumWindows(delegate(IntPtr h, IntPtr l) {
            uint p; GetWindowThreadProcessId(h, out p);
            if (p != pid) return true;
            EnumChildWindows(h, delegate(IntPtr ch, IntPtr l2) {
                var t = Title(ch);
                if (t.Length > 0) rows.Add(t);
                return true;
            }, IntPtr.Zero);
            return true;
        }, IntPtr.Zero);
        return rows.ToArray();
    }
}
'@
}

[UnityWin]::SetProcessDPIAware() | Out-Null

if ($ProcessId -eq 0) {
    $proc = Get-Process $ProcessName -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $proc) { throw "没有在跑的 $ProcessName 进程。" }
    $ProcessId = $proc.Id
}

$top = [UnityWin]::Top([uint32]$ProcessId)
if ($top -eq [IntPtr]::Zero) { throw "进程 $ProcessId 没有顶层窗口。" }

# 最小化的窗口没有可读的客户区，两条路都读不到东西，所以这一步与前台无关、照做。
if ([UnityWin]::IsIconic($top)) {
    [UnityWin]::ShowWindow($top, 9) | Out-Null   # SW_RESTORE
    Start-Sleep -Milliseconds 400
}

$target = $top
if ($View) {
    $target = [UnityWin]::Child([uint32]$ProcessId, $View)
    if ($target -eq [IntPtr]::Zero) {
        $names = [UnityWin]::Names([uint32]$ProcessId) -join '、'
        throw "找不到 $View。当前的子窗口有：$names（Game 与 Scene 抢同一个位置，没激活的那个没有句柄）"
    }
}

$rect = New-Object UnityWin+RECT
[UnityWin]::GetWindowRect($target, [ref]$rect) | Out-Null
$w = $rect.Right - $rect.Left
$h = $rect.Bottom - $rect.Top

if ($w -le 0 -or $h -le 0) {
    throw "窗口没有有效尺寸（${w}x${h}）。"
}

$dir = Split-Path -Parent $Out
if ($dir -and -not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }

# 平均亮度：抽样即可，这个数字只用来回答「是不是黑图」。
function Brightness([System.Drawing.Bitmap]$bmp) {
    $sum = 0; $n = 0
    for ($y = 0; $y -lt $bmp.Height; $y += 17) {
        for ($x = 0; $x -lt $bmp.Width; $x += 17) {
            $c = $bmp.GetPixel($x, $y)
            $sum += $c.R + $c.G + $c.B
            $n++
        }
    }
    if ($n -eq 0) { return 0 }
    return [math]::Round($sum / (3 * $n), 1)
}

# --- 第一条路：直接读 ---------------------------------------------------------------
$bmp = $null
$mode = 'direct'
$drawn = $false

if (-not $Screen) {
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc()
    try {
        $drawn = [UnityWin]::PrintWindow($target, $hdc, 2)   # PW_RENDERFULLCONTENT
    }
    finally {
        $g.ReleaseHdc($hdc)
        $g.Dispose()
    }
    if (-not $drawn) { $bmp.Dispose(); $bmp = $null }
}

# --- 第二条路：屏幕拷贝（回退，或者 -Screen 指定）----------------------------------
if ($null -eq $bmp -or (Brightness $bmp) -lt 1) {
    # 全黑就当作直接读没拿到内容（WebView2、某些 GPU 合成的窗口会这样）。
    if ($null -ne $bmp) { $bmp.Dispose(); $bmp = $null }

    if ($rect.Left -lt -1000 -or $rect.Right -lt 0) {
        throw "直接读没拿到内容，而窗口不在屏幕上（矩形 $($rect.Left),$($rect.Top)），屏幕拷贝这条路也走不了。"
    }

    # 只有 -Focus 才叫到最前。默认不叫：读到的东西不该取决于它有没有获得焦点。
    if ($Focus) { [UnityWin]::SetForegroundWindow($top) | Out-Null; Start-Sleep -Milliseconds 700 }

    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    try { $g.CopyFromScreen($rect.Left, $rect.Top, 0, 0, (New-Object System.Drawing.Size $w, $h)) }
    finally { $g.Dispose() }
    $mode = 'screen'
}

$brightness = Brightness $bmp
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()

[pscustomobject]@{
    ok         = $true
    path       = $Out
    bytes      = (Get-Item -LiteralPath $Out).Length
    width      = $w
    height     = $h
    at         = "$($rect.Left),$($rect.Top)"
    view       = $(if ($View) { $View } else { '(整个窗口)' })
    mode       = $mode
    brightness = $brightness
} | ConvertTo-Json -Compress
