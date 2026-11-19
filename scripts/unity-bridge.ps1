# unity-bridge.ps1 - 通过 .bridge 目录跟正在跑的 Unity Editor 说话。
#
# 这条路存在的原因是授权：这台机器上的 Unity 在 batchmode 下拿不到 headless entitlement，
# 所以编辑器不会被外部启动。但 GUI 授权是好的——于是换成让已经在跑的那个接命令。
#
# 协议很简单：写一个 <name>.cmd 到 .bridge/in，等 .bridge/out/<name>.json 出现。
# 写的时候先写成 .tmp 再改名，避免 Editor 读到半个文件。
#
# 用法：
#   & scripts\unity-bridge.ps1 status
#   & scripts\unity-bridge.ps1 play
#   & scripts\unity-bridge.ps1 stop
#   & scripts\unity-bridge.ps1 log
#   & scripts\unity-bridge.ps1 shot -Arg D:\Clarry\.tmp\stage.png
#   & scripts\unity-bridge.ps1 refresh
#
# 工程根默认 D:\Clarry\unity\ClarryStage，用 -Project 改。

param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$Command,
    [string]$Arg = '',
    [string]$Project = 'D:\Clarry\unity\ClarryStage',
    [int]$TimeoutSec = 15
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$inDir = Join-Path $Project '.bridge\in'
$outDir = Join-Path $Project '.bridge\out'
New-Item -ItemType Directory -Force -Path $inDir, $outDir | Out-Null

$name = $Command + '-' + (Get-Date -Format 'HHmmss-fff')
$tmp = Join-Path $inDir "$name.tmp"
$cmd = Join-Path $inDir "$name.cmd"
$out = Join-Path $outDir "$name.json"

$body = @{ command = $Command; arg = $Arg } | ConvertTo-Json -Compress
Set-Content -LiteralPath $tmp -Value $body -Encoding UTF8 -NoNewline
Move-Item -LiteralPath $tmp -Destination $cmd -Force

$deadline = (Get-Date).AddSeconds($TimeoutSec)
while ((Get-Date) -lt $deadline) {
    if (Test-Path -LiteralPath $out) {
        # Editor 可能刚创建文件还没写完，重试几次读
        for ($i = 0; $i -lt 10; $i++) {
            try {
                $json = Get-Content -LiteralPath $out -Raw -Encoding UTF8 | ConvertFrom-Json
                Remove-Item -LiteralPath $out -Force -ErrorAction SilentlyContinue
                return $json
            } catch {
                Start-Sleep -Milliseconds 80
            }
        }
        throw "回复文件读不动：$out"
    }
    Start-Sleep -Milliseconds 120
}

throw ("等了 {0} 秒没有回音。编辑器开着吗？桥脚本编译过了吗？（工程根：{1}）" -f $TimeoutSec, $Project)
