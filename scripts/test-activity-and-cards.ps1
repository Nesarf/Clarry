# test-activity-and-cards.ps1 - 验两样新东西：
#   一、agent 自己发明的活动（世界只记账，不解释规则）
#   二、黑白对局（有牌序，但同种子同牌序）

$base = 'http://127.0.0.1:3090'

function Get-J([string]$p) { Invoke-RestMethod -Uri "$base$p" -Method Get -TimeoutSec 30 }
function Post-J([string]$p, $b) {
    if ($null -eq $b) { Invoke-RestMethod -Uri "$base$p" -Method Post -TimeoutSec 60 }
    else {
        Invoke-RestMethod -Uri "$base$p" -Method Post -TimeoutSec 60 `
            -ContentType 'application/json' -Body ($b | ConvertTo-Json -Depth 12)
    }
}
function Who([string]$id) { (Get-J '/api/cast') | Where-Object { $_.id -eq $id } }

$fails = 0
function Check([string]$label, [bool]$ok, [string]$detail) {
    if ($ok) {
        Write-Host ("  通过  {0}{1}" -f $label, $(if ($detail) { "  （$detail）" } else { '' })) -ForegroundColor Green
    } else {
        Write-Host ("  失败  {0}{1}" -f $label, $(if ($detail) { "  （$detail）" } else { '' })) -ForegroundColor Red
        $script:fails++
    }
}

Write-Host '=== 0. 清场，三人在台'
Post-J '/api/loop/stop' $null | Out-Null
Post-J '/api/scene/new?clueGoal=99' $null | Out-Null
foreach ($id in @('alice', 'bob', 'carol')) { Post-J '/api/stage/enter' @{ id = $id } | Out-Null }
Check '三人在台' (((Get-J '/api/world').stage).Count -ge 3) ((Get-J '/api/world').stage -join '、')

Write-Host '=== 一、agent 自定义活动'
$open = Post-J '/api/activity/open' @{
    name         = '赌酒'
    hostId       = 'alice'
    rules        = '每人轮流说一件自己做过的事，对方判断真假；猜错喝一碗。三碗定胜负。'
    participants = @('alice', 'bob')
}
Check '活动开场' (-not [string]::IsNullOrWhiteSpace($open.opened)) ("{0}：{1}" -f $open.opened, $open.name)

$m1 = Post-J '/api/activity/move' @{
    activityId = $open.opened; actorId = 'alice'
    move = '我烧过一页写着我自己名字的纸'
}
Check '第一位已落子' (-not [bool]$m1.everyoneMoved) ("{0}/{1}" -f $m1.moves, $m1.participants)

$m2 = Post-J '/api/activity/move' @{
    activityId = $open.opened; actorId = 'bob'
    move = '我猜她说的是真的'
}
Check '人齐了' ([bool]$m2.everyoneMoved) ("{0}/{1}" -f $m2.moves, $m2.participants)

$before = Who 'alice'
$close = Post-J '/api/activity/close' @{
    activityId = $open.opened
    judgedBy   = 'alice'
    verdict    = '鲍勃猜错了。爱丽丝那句话是真的，他喝了三碗，站不稳。'
    effects    = @(
        @{ actorId = 'bob';   poise = 0; insight = 0; renown = -1; strain = 3; reserve = -2; ember = $false; bondTarget = 'alice'; bondDelta = 4 },
        @{ actorId = 'alice'; poise = 1; insight = 1; renown = 2;  strain = -1; reserve = 0; ember = $false; bondTarget = 'bob';   bondDelta = 2 }
    )
}
Check '活动已裁决' ($close.settled -eq $open.opened) $close.verdict

$after = Who 'alice'
Check '裁决带来的属性变动生效' (($after.a.strain - $before.a.strain) -eq -1) ("strain {0} → {1}" -f $before.a.strain, $after.a.strain)
Check '羁绊也按裁决变化' ($after.a.bond.bob -gt 0) ("alice → bob 温度 {0}" -f $after.a.bond.bob)

$list = Get-J '/api/activity'
Check '活动进了世界账本' (($list | Where-Object { $_.id -eq $open.opened }).Count -eq 1) ("账本里 {0} 场" -f $list.Count)

$again = 0
try { Post-J '/api/activity/close' @{ activityId = $open.opened; judgedBy = 'bob'; verdict = '再判一次' } | Out-Null; $again = 200 }
catch { $again = [int]$_.Exception.Response.StatusCode }
Check '同一场活动不能裁决两次' ($again -eq 409) ("HTTP {0}" -f $again)

Write-Host '=== 二、黑白对局'
$g1 = Post-J '/api/minigame/card-duel?a=alice&b=bob' $null
Check '分出胜负' (-not [string]::IsNullOrWhiteSpace($g1.winner)) ("胜者 {0}" -f $g1.winner)
Check '牌序被标记为有随机' ([bool]$g1.usedDice) ''
$g1.log | Select-Object -First 5 | ForEach-Object { '    ' + $_ }

$g2 = Post-J '/api/minigame/card-duel?a=alice&b=bob' $null
$sameSeed = $g1.log[0] -eq $g2.log[0]
Check '同一个世界重打，种子相同' $sameSeed $g1.log[0]
$sameOpen = $g1.log[1] -eq $g2.log[1]
Check '开局第一手也相同' $sameOpen ("{0} / {1}" -f $g1.log[1], $g2.log[1])

$g3 = Post-J '/api/minigame/card-duel?a=bob&b=carol' $null
Check '换人就是另一副牌' ($g1.log[0] -ne $g3.log[0]) ("{0} vs {1}" -f $g1.log[0], $g3.log[0])

Write-Host ''
if ($fails -eq 0) { Write-Host '全部检查通过' -ForegroundColor Green }
else { Write-Host ("{0} 项检查未通过" -f $fails) -ForegroundColor Red; exit 1 }
