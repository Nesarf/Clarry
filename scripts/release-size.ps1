# release-size.ps1 - 量体积，不猜。
#
# 三个数字是三件事：工作区能到 30~60 GB，release 必须压在 5 GB 以内，版本控制只要 500 MB。
# 这个脚本只报实测，不做判断，超了就照实说超了。

param(
    [string]$Root = 'D:\Clarry',
    [double]$WorkspaceLimitGB = 60,
    [double]$ReleaseLimitGB = 5,
    [string]$ReleaseDir = ''
)

$ErrorActionPreference = 'Stop'

function Get-Size([string]$path) {
    if (-not (Test-Path $path)) { return [long]0 }
    $m = Get-ChildItem $path -Recurse -File -ErrorAction SilentlyContinue | Measure-Object Length -Sum
    if ($null -eq $m.Sum) { return [long]0 }
    return [long]$m.Sum
}

if (-not (Test-Path $Root)) { throw "没有这个目录：$Root" }

Write-Host '=== 工作区'
$total = Get-Size $Root
'  {0,-10} {1,9:0.00} GB' -f '总计', ($total / 1GB)
'  {0,-10} {1,9:0.00} GB' -f '预算', $WorkspaceLimitGB
$used = if ($WorkspaceLimitGB -gt 0) { ($total / 1GB) / $WorkspaceLimitGB } else { 0 }
'  {0,-10} {1,9:P0}' -f '已用', $used

Write-Host ''
Write-Host '--- 按目录排（前 12）'
$rows = Get-ChildItem $Root -Directory -ErrorAction SilentlyContinue | ForEach-Object {
    [pscustomobject]@{ Name = $_.Name; GB = (Get-Size $_.FullName) / 1GB }
} | Sort-Object GB -Descending

$i = 0
foreach ($row in $rows) {
    $i++
    if ($i -gt 12) { break }
    '  {0,-18} {1,9:0.000} GB' -f $row.Name, $row.GB
}

Write-Host ''
Write-Host '=== 发布包'
if ([string]::IsNullOrWhiteSpace($ReleaseDir)) {
    Write-Host '  没给 -ReleaseDir，只做估算（服务端产物 + 资产）'
    $runtime = Get-Size (Join-Path $Root 'core\src\Clarry.Server\bin')
    $assets = Get-Size (Join-Path $Root 'assets')
    $estimate = $runtime + $assets
    '  服务端产物   {0,9:0.0} MB' -f ($runtime / 1MB)
    '  资产         {0,9:0.0} MB' -f ($assets / 1MB)
    '  合计估算     {0,9:0.00} GB / 上限 {1} GB' -f ($estimate / 1GB), $ReleaseLimitGB
    if (($estimate / 1GB) -gt $ReleaseLimitGB) {
        Write-Host '  按估算已经超限' -ForegroundColor Red
    } else {
        Write-Host '  按估算在上限之内' -ForegroundColor Green
    }
}
else {
    $size = Get-Size $ReleaseDir
    '  目录   {0}' -f $ReleaseDir
    '  体积   {0,9:0.00} GB / 上限 {1} GB' -f ($size / 1GB), $ReleaseLimitGB
    if (($size / 1GB) -gt $ReleaseLimitGB) {
        Write-Host '  超出上限，按体积列出里面最重的十项：' -ForegroundColor Red
        Get-ChildItem $ReleaseDir -Directory -ErrorAction SilentlyContinue | ForEach-Object {
            [pscustomobject]@{ Name = $_.Name; GB = (Get-Size $_.FullName) / 1GB }
        } | Sort-Object GB -Descending | Select-Object -First 10 |
            ForEach-Object { '    {0,-26} {1,9:0.000} GB' -f $_.Name, $_.GB }
    } else {
        Write-Host '  在上限之内' -ForegroundColor Green
    }
}
