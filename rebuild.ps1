# rebuild.ps1 - stop the running Clarry server, rebuild, optionally start it again.
# The server holds Clarry.Core.dll open, so a build without stopping it fails with MSB3027.
param([switch]$Start)
$ErrorActionPreference = 'Stop'
Get-Process -Name 'Clarry.Server' -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2
Push-Location $PSScriptRoot\core
try {
    dotnet build Clarry.slnx -v q --nologo
    if ($LASTEXITCODE -ne 0) { throw "编译失败，退出码 $LASTEXITCODE" }
} finally { Pop-Location }
if ($Start) {
    $exe = Join-Path $PSScriptRoot 'core\src\Clarry.Server\bin\Debug\net10.0\Clarry.Server.exe'

    # 输出必须写进文件，不能挂在父控制台上。服务每收到一个请求就往 stdout 写一行 REQ，
    # 而父 PowerShell 一退出，那个句柄就失效了——下一次写日志就是一次未处理的异常，
    # 进程静悄悄地死掉，看起来像是「服务自己掉了」。实测掉过两次，都是在我跑完命令之后。
    $logDir = Join-Path $PSScriptRoot 'data'
    New-Item -ItemType Directory -Force -Path $logDir | Out-Null
    $outLog = Join-Path $logDir 'server.log'
    $errLog = Join-Path $logDir 'server.err.log'

    Start-Process -FilePath $exe -ArgumentList '--urls','http://127.0.0.1:3090' -WindowStyle Hidden `
        -RedirectStandardOutput $outLog -RedirectStandardError $errLog
    Start-Sleep -Seconds 5
    Write-Host '[clarry] 服务已在 http://127.0.0.1:3090 上起好'
    Write-Host ("[clarry] 日志：{0} 与 {1}" -f $outLog, $errLog)
}