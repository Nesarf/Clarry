# unity-window.ps1 - 从外面读正在运行的 Unity 编辑器窗口，存成 PNG。
#
# 这条路不需要 Unity 的任何授权、模块或脚本：它抓的是 Windows 合成到屏幕上的那一片像素。
# 走过三条 Unity 内部的路都不通（ScreenCapture 不落盘、CaptureScreenshotAsTexture 返回空、
# 等一帧的协程要在运行时程序集里而那个程序集没有 ScreenCapture 模块），所以换到外面来。
#
# 两个前提，缺一个就抓不到：
#   1. 编辑器窗口不能最小化。脚本会替你还原并前置，除非给 -NoFocus。
#   2. 别让别的窗口盖在它上面——从屏幕拷像素，盖住的部分拷到的是盖子。
#
# 用法：
#   & scripts\unity-window.ps1                             抓整个编辑器
#   & scripts\unity-window.ps1 -View GameView              只抓 Game 视图
#   & scripts\unity-window.ps1 -View SceneView -Out D:\x.png
#   & scripts\unity-window.ps1 -NoFocus                    不抢焦点（窗口本来就在前台时用）
#
# 输出一行 JSON：路径、尺寸、平均亮度。亮度接近 0 就是黑图，说明窗口不在屏幕上。

param(
    [string]$Out = 'D:\Clarry\shots\unity.png',
    [int]$ProcessId = 0,
    [string]$View = '',
    [switch]$NoFocus
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
    $proc = Get-Process Unity -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $proc) { throw '没有在跑的 Unity 进程。' }
    $ProcessId = $proc.Id
}

$top = [UnityWin]::Top([uint32]$ProcessId)
if ($top -eq [IntPtr]::Zero) { throw "进程 $ProcessId 没有顶层窗口。" }

if ([UnityWin]::IsIconic($top)) {
    if ($NoFocus) { throw '窗口是最小化的，读了也是黑的。去掉 -NoFocus 让脚本把它叫起来。' }
    [UnityWin]::ShowWindow($top, 9) | Out-Null   # SW_RESTORE
}

if (-not $NoFocus) {
    [UnityWin]::SetForegroundWindow($top) | Out-Null
}

Start-Sleep -Milliseconds 700

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

if ($w -le 0 -or $h -le 0 -or $rect.Left -lt -1000) {
    throw "窗口不在屏幕上（矩形 $($rect.Left),$($rect.Top) ${w}x${h}）。"
}

$dir = Split-Path -Parent $Out
if ($dir -and -not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }

$bmp = New-Object System.Drawing.Bitmap $w, $h
$g = [System.Drawing.Graphics]::FromImage($bmp)
try {
    $g.CopyFromScreen($rect.Left, $rect.Top, 0, 0, (New-Object System.Drawing.Size $w, $h))
}
finally { $g.Dispose() }

$sum = 0
$n = 0
for ($y = 0; $y -lt $h; $y += 17) {
    for ($x = 0; $x -lt $w; $x += 17) {
        $c = $bmp.GetPixel($x, $y)
        $sum += $c.R + $c.G + $c.B
        $n++
    }
}

$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()

[pscustomobject]@{
    ok       = $true
    path     = $Out
    bytes    = (Get-Item -LiteralPath $Out).Length
    width    = $w
    height   = $h
    at       = "$($rect.Left),$($rect.Top)"
    view     = $(if ($View) { $View } else { '(整个编辑器)' })
    brightness = [math]::Round($sum / (3 * $n), 1)
} | ConvertTo-Json -Compress
