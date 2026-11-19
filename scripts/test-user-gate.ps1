# test-user-gate.ps1 - prove the one path nothing has walked yet: a round that points at
# a human seat must hold the loop until that human answers, then settle.
#
# The check is not "did it not crash". It is: while held, awaitingUser is true and the round
# does not advance; once the human declares, the round settles and the loop resumes.

$base = 'http://127.0.0.1:3090'

function Get-J([string]$p) { Invoke-RestMethod -Uri "$base$p" -Method Get -TimeoutSec 30 }
function Post-J([string]$p, $b) {
    if ($null -eq $b) { Invoke-RestMethod -Uri "$base$p" -Method Post -TimeoutSec 30 }
    else {
        Invoke-RestMethod -Uri "$base$p" -Method Post -TimeoutSec 30 `
            -ContentType 'application/json' -Body ($b | ConvertTo-Json -Depth 12)
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

Write-Host '=== 0. 清场'
Post-J '/api/loop/stop?reason=reset' $null | Out-Null
Post-J '/api/scene/new?clueGoal=3' $null | Out-Null

Write-Host '=== 1. 让一位玩家上台'
Post-J '/api/character' @{
    id       = 'sensei'
    name     = '老师'
    isPlayer = $true
    sheet    = @{
        role = '来访者'
        summary = '今晚只是来看一眼的人。'
        voice = '短句，问得很少。'
        traits = @(); goals = @(); fears = @(); secrets = @(); relations = @{}
    }
    visual   = @{ kind = 'none'; expressions = @() }
    agent    = @{ kind = 'human'; autonomy = 'free' }
} | Out-Null
Post-J '/api/stage/enter' @{ id = 'sensei' } | Out-Null
$stage = (Get-J '/api/world').stage -join ','
Check '玩家已经在台上' ($stage -like '*sensei*') $stage

Write-Host '=== 2. 开一轮，让爱丽丝指名问玩家'
Post-J '/api/round/begin' $null | Out-Null
Post-J '/api/round/order' @{
    actorId = 'alice'; kind = 'speak'; targetId = 'sensei'
    text = '门开着，你却一直站在门槛上。进来，还是走？'
} | Out-Null
$cur = Get-J '/api/round/current'
Check '指向玩家的行动已记录' (($cur.orders | Where-Object { $_.targetId -eq 'sensei' }).Count -eq 1) ("第 {0} 轮，{1} 条行动" -f $cur.round, $cur.orders.Count)

Write-Host '=== 3. 只给鲍勃挂驱动，然后起循环'
Post-J '/api/driver/attach' @{ id = 'bob'; mode = 'scripted' } | Out-Null
Post-J '/api/loop/start' @{ intervalMs = 150; maxRounds = 2; userWaitMs = 25000 } | Out-Null

Write-Host '=== 4. 循环必须停住，不许自己往下走'
Start-Sleep -Seconds 3
$st = Get-J '/api/loop/status'
Check '循环为玩家挂起' ([bool]$st.awaitingUser) ([string]$st.awaitingBecause)
Check '挂起期间这一轮没有结算' ($st.round -le $cur.round) ("第 {0} 轮" -f $st.round)
$st.log | Select-Object -Last 6 | ForEach-Object { '        ' + $_.text }

Write-Host '=== 5. 玩家接话'
Post-J '/api/round/order' @{
    actorId = 'sensei'; kind = 'speak'
    text = '我先进来。你们继续，当我不在。'
} | Out-Null

Start-Sleep -Seconds 4
$st2 = Get-J '/api/loop/status'
Check '挂起已解除' (-not [bool]$st2.awaitingUser) ''
Check '被挂起的那一轮已结算' ($st2.round -gt $st.round) ("第 {0} 轮 -> 第 {1} 轮" -f $st.round, $st2.round)

$turns = Get-J '/api/history?take=20'
$humanSpoke = ($turns | Where-Object { $_.actorId -eq 'sensei' -and $_.kind -eq 'speak' }).Count
Check '玩家那句话进了历史' ($humanSpoke -ge 1) ("{0} 句" -f $humanSpoke)

Write-Host '=== 6. 收尾：停循环，让玩家下台'
Post-J '/api/loop/stop?reason=切面验证完毕' $null | Out-Null
Post-J '/api/driver/detach' @{ id = 'bob' } | Out-Null
Post-J '/api/stage/leave' @{ id = 'sensei' } | Out-Null

Write-Host ''
if ($fails -eq 0) { Write-Host '全部检查通过' -ForegroundColor Green }
else { Write-Host ("{0} 项检查未通过" -f $fails) -ForegroundColor Red; exit 1 }
