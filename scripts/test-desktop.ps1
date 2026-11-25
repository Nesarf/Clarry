# test-desktop.ps1 - 独立窗口能不能自己起来，关掉之后世界是不是真的跟着停了。
#
# 这套和别的不一样：它开一个真窗口，占一个自己的端口，用一份自己的 data 目录。
# 要验的正是「窗口自己托管世界、关窗即停」，那件事只有在窗口独占一个世界时才看得清，
# 所以它不去碰 3090 上正在演的那一份。
#
# 另外两项专门验「读窗口不靠前台」：抓图走 unity-window.ps1 的直接读，并且把窗口挪到屏幕外
# 再读一次 —— 屏幕拷贝在那种位置只能拿到空，直接读照样有内容。这一条重要，因为
# 「读得到」如果依赖窗口获得焦点，验收本身就会去抢用户的焦点。
#
# 端口 3097 与 data/test-desktop 都是刻意的隔离。窗口用 CloseMainWindow() 关，
# 与用户点右上角那个叉是同一条消息 —— 用 Kill 验不出「关窗即停」，只能验出「杀进程会停」。

param(
    [string]$Base = 'http://127.0.0.1:3097',
    [int]$BootSeconds = 15,
    [switch]$KeepData
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [System.Text.Encoding]::UTF8

$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root 'core\src\Clarry.Desktop\bin\Debug\net10.0-windows\Clarry.Desktop.exe'
$dataDir = Join-Path $root 'data\test-desktop'

$script:fails = @()
$script:checks = 0

# 挪窗口用的最小 P/Invoke。只在这套里用，所以不进 unity-window.ps1。
if (-not ('WindowMover' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public class WindowMover {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
}
'@
}

function Check([string]$name, [scriptblock]$body) {
    $script:checks++
    try {
        $ok = & $body
        if ($ok) {
            Write-Host ("  通过  {0}" -f $name) -ForegroundColor Green
        } else {
            Write-Host ("  失败  {0}" -f $name) -ForegroundColor Red
            $script:fails += $name
        }
    } catch {
        Write-Host ("  失败  {0}（{1}）" -f $name, $_.Exception.Message) -ForegroundColor Red
        $script:fails += $name
    }
}

Write-Host '=== 独立窗口'

if (-not (Test-Path -LiteralPath $exe)) {
    Write-Host ("  失败  桌面壳还没编译：{0}" -f $exe) -ForegroundColor Red
    Write-Host '1 项检查未通过'
    exit 1
}

# 一个干净的世界：data 下面（被 .gitignore 覆盖），不写 C 盘，也不碰 3090 那一份。
if (Test-Path -LiteralPath $dataDir) { Remove-Item -LiteralPath $dataDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $dataDir | Out-Null

$env:CLARRY_ROOT = $root
$env:CLARRY_DATA = $dataDir

# 这套要验「窗口自己起服务」，所以端口必须先空着。
$occupied = $false
try { $null = Invoke-WebRequest "$Base/api/health" -TimeoutSec 2 -UseBasicParsing; $occupied = $true } catch { }
if ($occupied) {
    Write-Host ("  失败  {0} 上已经有东西在应答，这套要一个空端口" -f $Base) -ForegroundColor Red
    Write-Host '1 项检查未通过'
    exit 1
}

$proc = Start-Process -FilePath $exe -ArgumentList @('--urls', $Base) -PassThru

try {
    $booted = $false
    for ($i = 0; $i -lt ($BootSeconds * 2); $i++) {
        Start-Sleep -Milliseconds 500
        if ($proc.HasExited) { break }
        try { $null = Invoke-WebRequest "$Base/api/health" -TimeoutSec 2 -UseBasicParsing; $booted = $true; break } catch { }
    }

    Check '窗口进程起来了' { -not $proc.HasExited }
    Check '窗口自己有标题' { $proc.Refresh(); $proc.MainWindowTitle -eq 'Clarry' }
    Check '世界由窗口自己托管（端口有人应答）' { $booted }

    $health = $null
    try { $health = Invoke-RestMethod -Uri "$Base/api/health" -TimeoutSec 5 } catch { }
    Check '/api/health 报的是 clarry' { $health -and $health.service -eq 'clarry' }
    Check '世界是新建的（loaded 为否）' { $health -and $health.loaded -eq $false }
    Check '六十四席都在' { $health -and $health.capacity -eq 64 }

    # WebView2 的浏览器进程把用户数据目录写在命令行里，那是它属于这个窗口的证据。
    Check 'WebView2 引擎起来了' {
        $n = @(Get-CimInstance Win32_Process -Filter "Name='msedgewebview2.exe'" -ErrorAction SilentlyContinue |
            Where-Object { $_.CommandLine -and $_.CommandLine.IndexOf($dataDir, [StringComparison]::OrdinalIgnoreCase) -ge 0 }).Count
        $n -gt 0
    }

    # 舞台真的画出来了才算「看得见」：直接读窗口，抓一张图。
    # 要求 mode 是 direct：那说明这张图是在不抢前台的前提下拿到的，而这套验收要的正是不靠前台。
    $shot = Join-Path $root 'shots\desktop-test.png'
    $brightness = 0
    Check '舞台画出来了（直接读，不靠前台）' {
        $json = (& (Join-Path $PSScriptRoot 'unity-window.ps1') -ProcessId $proc.Id -Out $shot 2>&1 | Out-String).Trim()
        $script:shotJson = $json | ConvertFrom-Json
        $script:brightness = [double]$script:shotJson.brightness
        $script:shotJson.mode -eq 'direct' -and $script:brightness -gt 8
    }

    # 硬证明：窗口完全不在屏幕上时，屏幕拷贝只能拿到空，直接读照样有内容。
    Check '挪到屏幕外也读得到（不靠屏幕）' {
        $h = $proc.MainWindowHandle
        if ($h -eq [IntPtr]::Zero) { return $false }
        $r = New-Object WindowMover+RECT
        [WindowMover]::GetWindowRect($h, [ref]$r) | Out-Null
        $w = $r.Right - $r.Left
        $ht = $r.Bottom - $r.Top
        try {
            # SWP_NOSIZE(1) | SWP_NOZORDER(4) | SWP_NOACTIVATE(0x10)
            [WindowMover]::SetWindowPos($h, [IntPtr]::Zero, -20000, 0, 0, 0, 0x15) | Out-Null
            Start-Sleep -Milliseconds 700
            $offOut = (& (Join-Path $PSScriptRoot 'unity-window.ps1') -ProcessId $proc.Id `
                -Out (Join-Path $root 'shots\desktop-offscreen.png') 2>&1 | Out-String).Trim()
            $off = $offOut | ConvertFrom-Json
            $script:offscreenMode = $off.mode
            $off.mode -eq 'direct' -and $off.brightness -gt 8
        }
        finally {
            [WindowMover]::SetWindowPos($h, [IntPtr]::Zero, $r.Left, $r.Top, $w, $ht, 0x14) | Out-Null
            Start-Sleep -Milliseconds 300
        }
    }

    # 关窗：与用户点叉同一条消息，不是 Kill。
    Check '关窗之后进程自己退了' {
        $null = $proc.CloseMainWindow()
        $proc.WaitForExit(15000)
    }

    Start-Sleep -Milliseconds 800
    Check '关窗之后世界也停了' {
        try {
            $null = Invoke-WebRequest "$Base/api/health" -TimeoutSec 3 -UseBasicParsing
            $false
        } catch {
            $true
        }
    }
}
finally {
    if (-not $proc.HasExited) { $proc.Kill(); $null = $proc.WaitForExit(5000) }
    if (-not $KeepData) { Remove-Item -LiteralPath $dataDir -Recurse -Force -ErrorAction SilentlyContinue }
}

Write-Host ''
if ($script:fails.Count -gt 0) {
    Write-Host ("{0} 项检查未通过" -f $script:fails.Count) -ForegroundColor Red
    exit 1
}

Write-Host ("全部检查通过（{0} 项）" -f $script:checks) -ForegroundColor Green
exit 0
