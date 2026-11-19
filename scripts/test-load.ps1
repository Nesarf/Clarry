# test-load.ps1 - 验负载闸门：八个人同时要说话，只放四路过去。
#
# 这一套测的不是「能不能跑」，是「跑起来会不会淹掉对面」。判据只有一个：
# 同一刻在飞的驱动器数量，永远不超过配置的上限。

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

Write-Host '=== 0. 清场，建八个人上台'
Post-J '/api/loop/stop' $null | Out-Null
Start-Sleep -Milliseconds 400
Post-J '/api/scene/new?clueGoal=99' $null | Out-Null
foreach ($r in (Get-J '/api/tiers').residents) {
    if ($r.seated) { Post-J '/api/stage/leave' @{ id = $r.id } | Out-Null }
}

$names = @('a1', 'a2', 'a3', 'a4', 'a5', 'a6', 'a7', 'a8')
foreach ($id in $names) {
    Post-J '/api/character' @{
        id = $id; name = $id; isPlayer = $false
        sheet  = @{ role = '居民'; traits = @(); goals = @(); fears = @(); secrets = @(); relations = @{} }
        visual = @{ kind = 'none'; expressions = @() }
        agent  = @{ kind = 'llm'; autonomy = 'guided' }
    } | Out-Null
    Post-J '/api/stage/enter' @{ id = $id } | Out-Null
}

$status = Get-J '/api/loop/status'
Check '状态里有并发上限' ($status.maxConcurrent -ge 1) ("maxConcurrent {0}" -f $status.maxConcurrent)
$cap = $status.maxConcurrent
Check '没有在跑的时候，在飞的是零' ($status.inFlight -eq 0) ("inFlight {0}" -f $status.inFlight)

Write-Host '=== 1. 八个人都挂上「要花一点时间才答」的驱动器'
foreach ($id in $names) {
    Post-J '/api/driver/attach' @{ id = $id; mode = 'scripted'; delayMs = 250 } | Out-Null
}
$driven = (Get-J '/api/driver/list').driven
Check '八个人都被驱使' ((@($driven) | Where-Object { $names -contains $_ }).Count -eq 8) ("{0} 个" -f (@($driven) | Where-Object { $names -contains $_ }).Count)

Write-Host '=== 2. 跑一拍：八个人同时要说话，闸门只放四路'
Post-J '/api/loop/start' @{ intervalMs = 50; maxRounds = 1 } | Out-Null

$waited = 0
$status = $null
while ($waited -lt 60000) {
    Start-Sleep -Milliseconds 200
    $waited += 200
    $status = Get-J '/api/loop/status'
    if (-not $status.running) { break }
}
Check '一拍之后自己收了场' ($status -and -not $status.running) $(if ($status) { $status.stopReason } else { '拿不到状态' })

Write-Host '=== 3. 峰值没有超过上限'
Check '峰值正好是上限，不是八' ($status.peakInFlight -eq $cap) ("peak {0} / 上限 {1} / 八个人" -f $status.peakInFlight, $cap)
Check '峰值没有越界' ($status.peakInFlight -le $status.maxConcurrent) ("peak {0}" -f $status.peakInFlight)
Check '跑完之后在飞的是零' ($status.inFlight -eq 0) ("inFlight {0}" -f $status.inFlight)

Write-Host '=== 4. 日志里留下了这一拍的账'
# 日志是跨 run 累积的：单独跑这一套时它只有一份，放进全套里前面几套也开过场。
# 所以只断言「有」，并且看最后一条。
$lines = @($status.log | ForEach-Object { $_.text })
$opens = @($lines | Where-Object { $_ -match '开场' })
$gates = @($lines | Where-Object { $_ -match '闸门' })
$asks = @($lines | Where-Object { $_ -match '问了话' })
Check '有开场那一行' ($opens.Count -ge 1) ($opens[-1])
Check '写了闸门是多少' ($gates.Count -ge 1) ($gates[-1])
Check '八个人都被问过话' (($asks | Where-Object { $_ -match '向 8 位问了话' }).Count -ge 1) ($asks[-1])

Write-Host '=== 5. 第二拍不再问所有人'
# 第一拍每个人都得开口，否则戏起不来。第二拍只有轮到的那个有理由说话——这就是
# 「席位上是 agent，不是 llm」在负载上的样子：安静的人不需要被问。
Post-J '/api/loop/start' @{ intervalMs = 50; maxRounds = 1 } | Out-Null
$waited = 0
$second = $null
while ($waited -lt 60000) {
    Start-Sleep -Milliseconds 200
    $waited += 200
    $second = Get-J '/api/loop/status'
    if (-not $second.running) { break }
}
$lines2 = @($second.log | ForEach-Object { $_.text })
$lastAsk = @($lines2 | Where-Object { $_ -match '问了话' })[-1]
Check '第二拍只问了轮到的那个' ($lastAsk -match '向 1 位问了话') $lastAsk
Check '写明了有几位不必开口' ($lastAsk -match '7 位没有开口的理由') $lastAsk

Write-Host '=== 6. 收场之后闸门干净'
Post-J '/api/loop/stop' $null | Out-Null
$idle = Get-J '/api/loop/status'
Check '没有残留的在飞' ($idle.inFlight -eq 0) ("inFlight {0}" -f $idle.inFlight)

Write-Host ''
if ($fails -eq 0) { Write-Host '全部检查通过' -ForegroundColor Green }
else { Write-Host ("{0} 项检查未通过" -f $fails) -ForegroundColor Red; exit 1 }
