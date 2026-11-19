# test-all.ps1 - 一条命令跑完全部验收，最后只说一句结论。
#
# 顺序不是随便排的：先属性与记忆这类不依赖任何别的东西的，再回合、再幕、再多幕，
# 最后才是建在多幕之上的场所、班次与视线。这样一旦某套失败，前面那些已经证明是好的，
# 不必从头猜起。
#
# 用法：
#   & scripts\test-all.ps1                 跑全部
#   & scripts\test-all.ps1 -Only test-wish 只跑一套（可以给多套）
#   & scripts\test-all.ps1 -WithDiagnostics  连 test-loop 那个调试脚本一起跑
#
# 服务要先起来：& rebuild.ps1 -Start

param(
    [string[]]$Only = @(),
    [switch]$WithDiagnostics,
    [string]$Base = 'http://127.0.0.1:3090'
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [System.Text.Encoding]::UTF8

# 顺序 = 依赖顺序。加新测试时插到它依赖的那一套后面。
$suites = @(
    'test-traits'
    'test-prompt-identity'
    'test-souls'
    'test-memory'
    'test-wish'
    'test-plea'
    'test-shrine'
    'test-address-and-vigil'
    'test-activity-and-cards'
    'test-user-gate'
    'test-scene-picker'
    'test-scenes'
    'test-multi-loop'
    'test-load'
    'test-runs'
    'test-audience'
    'test-divination'
    'test-venues'
    'test-shift'
    'test-board-game'
    'test-expression'
    'test-lineweight'
)

# test-loop 只打印状态、不下判断，所以它是诊断用的，不算验收的一格。
if ($WithDiagnostics) { $suites += 'test-loop' }

if ($Only.Count -gt 0) {
    $suites = @($suites | Where-Object { $Only -contains $_ })
    if ($suites.Count -eq 0) {
        Write-Host ("没有叫这些名字的测试：" + ($Only -join '、')) -ForegroundColor Red
        exit 1
    }
}

Write-Host '=== 先确认服务在跑'
try {
    $null = Invoke-RestMethod -Uri "$Base/api/world" -Method Get -TimeoutSec 5
} catch {
    Write-Host ("  连不上 {0}" -f $Base) -ForegroundColor Red
    Write-Host '  先把服务起起来：& D:\Clarry\rebuild.ps1 -Start' -ForegroundColor Yellow
    exit 1
}
Write-Host ("  在跑（{0}）" -f $Base) -ForegroundColor Green

# 一套算通过还是没通过。
#   有「项检查未通过」         → 失败
#   有行首两空格的「失败」     → 失败
#   有「全部检查通过」         → 通过
#   都没有                     → 没有判据（诊断脚本会这样）
function Verdict([string]$text) {
    if ($text -match '项检查未通过') { return 'failed' }
    if ($text -match '(?m)^  失败') { return 'failed' }
    if ($text -match '全部检查通过') { return 'passed' }
    return 'unknown'
}

$script:root = Split-Path -Parent $PSScriptRoot
$results = @()
$started = Get-Date

Write-Host ''
Write-Host ("=== 跑 {0} 套" -f $suites.Count)
Write-Host ''

$i = 0
foreach ($name in $suites) {
    $i++
    $path = Join-Path $PSScriptRoot "$name.ps1"
    $label = '{0,2}  {1,-24}' -f $i, $name

    if (-not (Test-Path -LiteralPath $path)) {
        Write-Host ($label + '缺这个文件') -ForegroundColor Red
        $results += [pscustomobject]@{ Name = $name; Verdict = 'missing'; Seconds = 0; Failed = @('脚本不存在') }
        continue
    }

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $out = (& $path *>&1 | Out-String)
    $sw.Stop()
    $secs = [math]::Round($sw.Elapsed.TotalSeconds, 1)

    $verdict = Verdict $out
    $failed = @([regex]::Matches($out, '(?m)^\s*失败\s.*$') | ForEach-Object { $_.Value.Trim() })

    $color = switch ($verdict) {
        'passed'  { 'Green' }
        'failed'  { 'Red' }
        default   { 'DarkGray' }
    }
    $mark = switch ($verdict) {
        'passed'  { '通过' }
        'failed'  { '未通过' }
        default   { '无判据' }
    }

    Write-Host ($label + ("{0}  {1}s" -f $mark, $secs)) -ForegroundColor $color

    $results += [pscustomobject]@{ Name = $name; Verdict = $verdict; Seconds = $secs; Failed = $failed }
}

$elapsed = [math]::Round(((Get-Date) - $started).TotalSeconds, 1)
$passed = @($results | Where-Object { $_.Verdict -eq 'passed' })
$failedSuites = @($results | Where-Object { $_.Verdict -eq 'failed' -or $_.Verdict -eq 'missing' })
$unknown = @($results | Where-Object { $_.Verdict -eq 'unknown' })

Write-Host ''
if ($failedSuites.Count -gt 0) {
    Write-Host '=== 没过的地方' -ForegroundColor Red
    foreach ($r in $failedSuites) {
        Write-Host ("  {0}" -f $r.Name) -ForegroundColor Red
        foreach ($line in $r.Failed) { Write-Host ("    " + $line) -ForegroundColor DarkRed }
    }
    Write-Host ''
    Write-Host ("{0} 套未通过（通过 {1}/{2}），用时 {3} 秒。" -f $failedSuites.Count, $passed.Count, $results.Count, $elapsed) -ForegroundColor Red
    exit 1
}

if ($unknown.Count -gt 0) {
    Write-Host ''
    foreach ($r in $unknown) {
        Write-Host ("  {0} 没有判据，只打印了状态，不计入通过数" -f $r.Name) -ForegroundColor DarkGray
    }
    Write-Host ("{0} 套通过，{1} 套无判据，用时 {2} 秒。" -f $passed.Count, $unknown.Count, $elapsed) -ForegroundColor Green
    exit 0
}

Write-Host ("全部通过（{0}/{1}），用时 {2} 秒。" -f $passed.Count, $results.Count, $elapsed) -ForegroundColor Green
