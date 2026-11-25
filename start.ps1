# start.ps1 - Clarry 的入口：双击、或者在这儿敲一句。
#
# 起的是桌面窗口（core/src/Clarry.Desktop），不是控制台服务。窗口自己会看 3090 上有没有人应答：
# 没有就在自己进程里起一个世界，有就连上去。所以这个脚本重复跑不会开出第二个世界，
# 关掉窗口也不会带走一个不归它管的世界。
#
# 想要只有 HTTP 表面（给 Unity、给别的客户端、给这台机器上的脚本）：
#     & .\rebuild.ps1 -Start
#
# 用法：
#     & .\start.ps1                     起窗口（没编译过就先编译）
#     & .\start.ps1 -Port 3091          换个端口
#     & .\start.ps1 -Build              强制先编译
#     & .\start.ps1 -Wait               等窗口关掉再返回（调试用）

param(
    [int]$Port = 3090,
    [switch]$Build,
    [switch]$Wait
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

# The window reads this to find stage/ and assets/, and the host reads it for data/.
# Setting it here rather than inside the exe is what lets the exe run from any directory.
$env:CLARRY_ROOT = $root

$exe = Join-Path $root 'core\src\Clarry.Desktop\bin\Debug\net10.0-windows\Clarry.Desktop.exe'

if ($Build -or -not (Test-Path -LiteralPath $exe)) {
    Push-Location (Join-Path $root 'core')
    try {
        dotnet build Clarry.slnx -v q --nologo
        if ($LASTEXITCODE -ne 0) { throw "编译失败，退出码 $LASTEXITCODE" }
    }
    finally { Pop-Location }
}

$url = "http://127.0.0.1:$Port"
Write-Host "[clarry] 窗口打开中：$url/stage"

$args = @('--urls', $url)
if ($Wait) {
    Start-Process -FilePath $exe -ArgumentList $args -Wait
} else {
    Start-Process -FilePath $exe -ArgumentList $args
}
