# test-shift.ps1 - 验班次：队列不是回合、合口味给的更多、队排满了人就走、
# 一单没做成不是败北、打烊时没等到的人也要记账、口碑是那条双刃的正反馈。

$base = 'http://127.0.0.1:3090'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [System.Text.Encoding]::UTF8

function Get-J([string]$p) { Invoke-RestMethod -Uri "$base$p" -Method Get -TimeoutSec 60 }
function Post-J([string]$p, $b) {
    if ($null -eq $b) { Invoke-RestMethod -Uri "$base$p" -Method Post -TimeoutSec 60 }
    else {
        Invoke-RestMethod -Uri "$base$p" -Method Post -TimeoutSec 60 `
            -ContentType 'application/json' -Body ($b | ConvertTo-Json -Depth 12)
    }
}
function Status-Of([scriptblock]$call) {
    try { $null = & $call; 200 } catch { [int]$_.Exception.Response.StatusCode }
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

Write-Host '=== 0. 建人：店主、店员、五个客人'
Post-J '/api/loop/stop' $null | Out-Null
Start-Sleep -Milliseconds 300

foreach ($id in @('keeper', 'guest1', 'guest2', 'guest3', 'guest4', 'guest5')) {
    Post-J '/api/character' @{
        id = $id; name = $id; isPlayer = $false
        sheet  = @{ role = '居民'; traits = @(); goals = @(); fears = @(); secrets = @(); relations = @{} }
        visual = @{ kind = 'none'; expressions = @() }
        agent  = @{ kind = 'llm'; autonomy = 'guided' }
    } | Out-Null
}
Post-J '/api/character' @{
    id = 'clerk'; name = 'clerk'; isPlayer = $false; tier = 'supporting'
    sheet  = @{ role = '店员'; traits = @(); goals = @(); fears = @(); secrets = @(); relations = @{} }
    visual = @{ kind = 'none'; expressions = @() }
    agent  = @{ kind = 'llm'; autonomy = 'guided' }
} | Out-Null

Write-Host '=== 1. 开店'
$shopName = '巷口面摊-' + (Get-Date -Format 'HHmmss')
$venue = Post-J '/api/venue' @{ ownerId = 'keeper'; name = $shopName; kind = 'bar' }
$vid = $venue.id

Write-Host '=== 2. 空菜单开不了班'
$code = Status-Of { Post-J '/api/venue/shift/open' @{ venueId = $vid } }
Check '菜单空着就不开张' ($code -eq 409) ("HTTP {0}" -f $code)

Post-J '/api/venue/menu' @{ venueId = $vid; name = '热汤面'; price = 5; effort = 1
    needs = @(@{ item = '面'; count = 1 }) } | Out-Null
Post-J '/api/venue/menu' @{ venueId = $vid; name = '冷酒'; price = 3; effort = 1
    needs = @(@{ item = '酒'; count = 1 }) } | Out-Null

Write-Host '=== 3. 没开门开不了班'
$code = Status-Of { Post-J '/api/venue/shift/open' @{ venueId = $vid } }
Check '门还关着就不开张' ($code -eq 409) ("HTTP {0}" -f $code)

Post-J '/api/venue/toggle' @{ venueId = $vid; open = $true } | Out-Null
Post-J '/api/venue/staff' @{ venueId = $vid; staffId = 'clerk'; wage = 2 } | Out-Null

$opened = Post-J '/api/venue/shift/open' @{ venueId = $vid }
Check '这一班开起来了' ($opened.open) ("capacity {0}，口碑 {1}" -f $opened.capacity, $opened.reputation)
$capacity = $opened.capacity

Write-Host '=== 4. 一单没做成，只是这一单没做成'
Post-J '/api/venue/shift/arrive' @{ venueId = $vid; guestId = 'guest1'; want = '热汤面' } | Out-Null
$missed = Status-Of { Post-J '/api/venue/shift/serve' @{ venueId = $vid; serverId = 'clerk'; dish = '热汤面' } }
Check '没有面就出不了面' ($missed -eq 409) ("HTTP {0}" -f $missed)

$afterMiss = Get-J "/api/venue/$vid"
Check '口碑掉了一点' ($afterMiss.reputation -eq 49) ("口碑 {0}" -f $afterMiss.reputation)
Check '人已经走了，队伍空的' ($afterMiss.shift.inLine -eq 0) ("排队 {0} 人，走了 {1} 人" -f $afterMiss.shift.inLine, $afterMiss.shift.walkedOut)

Write-Host '=== 5. 补了货，合口味的客人给两分'
Post-J '/api/venue/stock' @{ venueId = $vid; item = '面'; count = 6; unitCost = 1; payerId = 'keeper' } | Out-Null
Post-J '/api/venue/stock' @{ venueId = $vid; item = '酒'; count = 2; unitCost = 1; payerId = 'keeper' } | Out-Null

Post-J '/api/venue/shift/arrive' @{ venueId = $vid; guestId = 'guest1'; want = '热汤面' } | Out-Null
$first = Post-J '/api/venue/shift/serve' @{ venueId = $vid; serverId = 'clerk'; dish = '热汤面' }
Check '他要的就是这碗，成了' ($first.dish -eq '热汤面') ("{0} 付了 {1}" -f $first.guest, $first.price)
Check '合口味，口碑 +2' ($first.reputation -eq 51) ("口碑 49 → {0}" -f $first.reputation)

Write-Host '=== 6. 要的不是这碗，也照付，只是不加分'
Post-J '/api/venue/shift/arrive' @{ venueId = $vid; guestId = 'guest2'; want = '冷酒' } | Out-Null
$wrong = Post-J '/api/venue/shift/serve' @{ venueId = $vid; serverId = 'clerk'; dish = '热汤面' }
Check '他点了酒，拿到面，钱照付' ($wrong.price -eq 5) ("出了 {0}，付了 {1}" -f $wrong.dish, $wrong.price)
Check '不合口味，口碑不加' ($wrong.reputation -eq 51) ("口碑仍是 {0}" -f $wrong.reputation)

Write-Host '=== 7. 没提要求的客人，给一分'
Post-J '/api/venue/shift/arrive' @{ venueId = $vid; guestId = 'guest3' } | Out-Null
$plain = Post-J '/api/venue/shift/serve' @{ venueId = $vid; serverId = 'clerk'; dish = '冷酒' }
Check '随老板做主的客人，口碑 +1' ($plain.reputation -eq 52) ("口碑 51 → {0}" -f $plain.reputation)

Write-Host '=== 8. 打烊，结这一班的账'
$report = Post-J '/api/venue/shift/close' @{ venueId = $vid }
Check '这一班做了三单' ($report.served -eq 3) ("served {0}" -f $report.served)
Check '这一班走了一个' ($report.walkedOut -eq 1) ("walkedOut {0}" -f $report.walkedOut)
Check '收入是正的' ($report.takings -gt 0) ("takings {0}" -f $report.takings)
Check '口碑从 50 走到 52' (($report.reputationBefore -eq 50) -and ($report.reputationAfter -eq 52)) ("{0} → {1}" -f $report.reputationBefore, $report.reputationAfter)

Write-Host '=== 9. 队排满了，人就走了——这是口碑的双刃'
$second = Post-J '/api/venue/shift/open' @{ venueId = $vid }
Check '第二天开张，容量跟着口碑走' ($second.capacity -eq $capacity) ("capacity {0}（口碑 {1}，{2} 人在值）" -f $second.capacity, $second.reputation, 1)

foreach ($g in @('guest1', 'guest2', 'guest3', 'guest4')) {
    Post-J '/api/venue/shift/arrive' @{ venueId = $vid; guestId = $g } | Out-Null
}
$overflow = Get-J "/api/venue/$vid"
Check '排到了容量上限' ($overflow.shift.inLine -eq $capacity) ("{0} 人 / 上限 {1}" -f $overflow.shift.inLine, $capacity)

$code = Status-Of { Post-J '/api/venue/shift/arrive' @{ venueId = $vid; guestId = 'guest5' } }
Check '第五个人进不来' ($code -eq 409) ("HTTP {0}" -f $code)

$afterFull = Get-J "/api/venue/$vid"
Check '进不来的人让口碑掉了一点' ($afterFull.reputation -eq 51) ("口碑 52 → {0}" -f $afterFull.reputation)

Write-Host '=== 10. 打烊时还站着的，也要算进账'
$report2 = Post-J '/api/venue/shift/close' @{ venueId = $vid }
# 五个人：队满被拒的一个，加上打烊时还站着的四个。
Check '这一班一共走了五个' ($report2.walkedOut -eq 5) ("walkedOut {0}（1 个没进来 + 4 个没等到）" -f $report2.walkedOut)
Check '口碑为此掉了四分' ($report2.reputationAfter -eq 47) ("{0} → {1}" -f $report2.reputationBefore, $report2.reputationAfter)
Check '报告里写明了还有人没等到' (($report2.notes -join ' / ') -match '没等到') ($report2.notes -join ' / ')

Write-Host '=== 11. 开班两次不重叠'
$third = Post-J '/api/venue/shift/open' @{ venueId = $vid }
$code = Status-Of { Post-J '/api/venue/shift/open' @{ venueId = $vid } }
Check '一班没打烊不能再开一班' ($code -eq 409) ("HTTP {0}" -f $code)
Post-J '/api/venue/shift/close' @{ venueId = $vid } | Out-Null

Write-Host '=== 12. 存档：店的账留下，队伍不留'
Post-J '/api/save' $null | Out-Null
$saved = Get-J "/api/venue/$vid"
# 50 → 51（合口味 +2 之前先掉了一分）→ 52 → 51（队满）→ 47（四个人没等到）
Check '口碑存下来了' ($saved.reputation -eq 47) ("口碑 {0}" -f $saved.reputation)
Check '开过几班存下来了' ($saved.daysOpen -ge 2) ("daysOpen {0}" -f $saved.daysOpen)
Check '账本还在' ($saved.ledger.Count -gt 0) ("{0} 条账" -f $saved.ledger.Count)
Check '打烊之后没有进行中的班次' ($null -eq $saved.shift) ("shift {0}" -f $saved.shift)

Write-Host ''
if ($fails -eq 0) { Write-Host '全部检查通过' -ForegroundColor Green }
else { Write-Host ("{0} 项检查未通过" -f $fails) -ForegroundColor Red; exit 1 }
