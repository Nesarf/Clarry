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
    Start-Process -FilePath $exe -ArgumentList '--urls','http://127.0.0.1:3090' -WindowStyle Hidden
    Start-Sleep -Seconds 5
    Write-Host '[clarry] 服务已在 http://127.0.0.1:3090 上起好'
}