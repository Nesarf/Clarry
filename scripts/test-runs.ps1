# test-runs.ps1 - 验运行账本：每一拍在盘上留一行，能回答「昨天那一场花了多少」。
#
# 这一套的判据不是「跑起来了」，是「跑完之后查得清」：问了几个人、几个人没理由开口、
# 同时最多几路、这一拍吃了多少 prompt。

$base = 'http://127.0.0.1:3090'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [System.Text.Encoding]::UTF8

function Get-J([string]$p) { Invoke-RestMethod -Uri "$base$p" -Method Get -TimeoutSec 60 }
function Post-J([string]$p, $b) {
    if ($null -eq $b) { Invoke-RestMethod -Uri "$base$p" -Method Post -TimeoutSec 60 }
    else {
        Invoke-RestMethod -Uri "$base$p" -Method Post -TimeoutSec 60 `
            -ContentType 'application/json' -Body ($b | ConvertTo-Json -Depth 10)
    }
}

$fails = 0
function Check([string]$label, [bool]$ok, [string]$detail) {
    if ($ok) {
        Write-Host ("  通过  {0}{1}" -f $label, $(if ($detail) { "  （$detail）" } else { '' })) -ForegroundColor Green
    } else {
        Write-Host ("  失败  {0}{1}" -f $label, $(if ($detail) { "  （$detail）" } else { '' })) -ForegroundColor Red
        $script:fails++
    }
}

Write-Host '=== 0. 清场，建四个人挂上驱动器'
Post-J '/api/loop/stop' $null | Out-Null
Start-Sleep -Milliseconds 400
Post-J '/api/scene/new?clueGoal=99' $null | Out-Null
foreach ($r in (Get-J '/api/tiers').residents) {
    if ($r.seated) { Post-J '/api/stage/leave' @{ id = $r.id } | Out-Null }
}

foreach ($id in @('r1', 'r2', 'r3', 'r4')) {
    Post-J '/api/character' @{
        id = $id; name = $id; isPlayer = $false
        sheet  = @{ role = '居民'; traits = @(); goals = @(); fears = @(); secrets = @(); relations = @{} }
        visual = @{ kind = 'none'; expressions = @() }
        agent  = @{ kind = 'llm'; autonomy = 'guided' }
    } | Out-Null
    Post-J '/api/stage/enter' @{ id = $id } | Out-Null
    Post-J '/api/driver/attach' @{ id = $id; mode = 'scripted' } | Out-Null
}

$before = (Get-J '/api/runs?take=2000').beats
Write-Host ("  跑之前账上有 {0} 行" -f $before)

Write-Host '=== 1. 跑两拍'
Post-J '/api/loop/start' @{ intervalMs = 50; maxRounds = 2 } | Out-Null
$waited = 0
$status = $null
while ($waited -lt 60000) {
    Start-Sleep -Milliseconds 200
    $waited += 200
    $status = Get-J '/api/loop/status'
    if (-not $status.running) { break }
}
Check '两拍之后自己收了场' ($status -and -not $status.running) $(if ($status) { $status.stopReason } else { '拿不到状态' })

Write-Host '=== 2. 账本多了两行'
$ledger = Get-J '/api/runs?take=2000'
Check '多了两行' ($ledger.beats -eq ($before + 2)) ("{0} → {1}" -f $before, $ledger.beats)

$rows = @($ledger.rows)
$lastTwo = @($rows[($rows.Count - 2)..($rows.Count - 1)])
Check '两行都记了拍数' (@($lastTwo | Where-Object { $_.beat -gt 0 }).Count -eq 2) (($lastTwo | ForEach-Object { $_.beat }) -join '、')

Write-Host '=== 3. 每一行能看出这一拍花了什么'
$first = $lastTwo[0]
$second = $lastTwo[1]
foreach ($field in @('asked', 'quiet', 'peakInFlight', 'promptChars', 'settled', 'elapsedMs')) {
    $has = ($first.PSObject.Properties.Name -contains $field)
    Check ("记下了 {0}" -f $field) $has ("{0}" -f $first.$field)
}

Write-Host '=== 4. 第一拍问全员，第二拍只问该问的'
Check '第一拍问了四个人' ($first.asked -eq 4) ("asked {0} / quiet {1}" -f $first.asked, $first.quiet)
Check '第二拍只问了一个' ($second.asked -eq 1) ("asked {0} / quiet {1}" -f $second.asked, $second.quiet)
Check '第二拍有三个人没理由开口' ($second.quiet -eq 3) ("quiet {0}" -f $second.quiet)
Check '两拍都结算了这一幕' ($first.settled -eq 1 -and $second.settled -eq 1) ("settled {0} / {1}" -f $first.settled, $second.settled)

Write-Host '=== 5. 汇总数字自洽'
$sumAsked = (@($rows | Measure-Object -Property asked -Sum).Sum)
Check '汇总的 asked 等于各行之和' ($ledger.asked -eq $sumAsked) ("{0} / {1}" -f $ledger.asked, $sumAsked)
# 脚本驱动器不发 prompt，所以这一列对它恒为零。断言的是「这一列在」，不是「它大于零」——
# 假装测了 token，比没测更糟。真实的数字要等 HTTP 驱动接上去才有。
Check '记下了吃掉的字数这一列' ($null -ne $ledger.promptChars) ("{0} 字（脚本驱动器没有 prompt）" -f $ledger.promptChars)

Write-Host '=== 6. 台账在盘上'
Write-Host ("  {0}" -f $ledger.ledger)
Check '说出了文件在哪' ($ledger.ledger.Length -gt 0) $ledger.ledger
Check '文件真的在' (Test-Path -LiteralPath $ledger.ledger) '在盘上'
if (Test-Path -LiteralPath $ledger.ledger) {
    $lines = @(Get-Content -LiteralPath $ledger.ledger -Encoding UTF8)
    Check '一行一拍，是 JSONL' ($lines.Count -ge $ledger.beats) ("{0} 行" -f $lines.Count)
    $parsed = $null
    try { $parsed = $lines[-1] | ConvertFrom-Json } catch { }
    Check '最后一行解得开' ($null -ne $parsed -and $parsed.beat -gt 0) $(if ($parsed) { "beat {0}" -f $parsed.beat } else { '读不动' })
}

Write-Host ''
if ($fails -eq 0) { Write-Host '全部检查通过' -ForegroundColor Green }
else { Write-Host ("{0} 项检查未通过" -f $fails) -ForegroundColor Red; exit 1 }
