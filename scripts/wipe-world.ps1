# wipe-world.ps1 - 把世界清回到没人来过的样子。
#
# 与 /api/scene/new 的区别值得分清：那个只清这一出戏的历史与回合账，演员留着，
# 因为他们还记得上一场；这个连演员一起清。留下的只有这个地点本身——天上那片天、
# 庭前那道石阶、阶上那位神，以及世界自己的名字。
#
# 默认只干跑，把会清掉什么先说出来。-Confirm 才真的清。这个区别不是谨慎过头：
# 清空之后没有撤销，而世界存档里那些角色是别人一句一句演出来的。
#
# 用法：
#   & scripts\wipe-world.ps1                    看会清掉什么
#   & scripts\wipe-world.ps1 -Confirm           真清
#   & scripts\wipe-world.ps1 -Confirm -Base <url>

param(
    [switch]$Confirm,
    [string]$Base = 'http://127.0.0.1:3090'
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$before = Invoke-RestMethod -Uri "$Base/api/world" -Method Get -TimeoutSec 30
$tiers = Invoke-RestMethod -Uri "$Base/api/tiers" -Method Get -TimeoutSec 30
$health = Invoke-RestMethod -Uri "$Base/api/health" -Method Get -TimeoutSec 30

if ($health.looping) {
    throw '循环正在跑。先 /api/loop/stop 收场，再清空。'
}

Write-Host '=== 现在这个世界' -ForegroundColor Cyan
Write-Host ('  角色 {0} 位（主角 {1}，店员 {2}），在台上 {3} 位' -f `
        $before.registry, $tiers.principals, $tiers.supporting, $tiers.seated)
Write-Host ('  幕 {0} 个（开着 {1}，收过 {2}），历史 {3} 条，回合 {4} 个' -f `
        ($before.scenes.open + $before.scenes.closed), $before.scenes.open, $before.scenes.closed, `
    $before.turns, $before.rounds)

if (-not $Confirm) {
    Write-Host ''
    Write-Host '这是干跑。加 -Confirm 才会真清。' -ForegroundColor Yellow
    exit 0
}

$r = Invoke-RestMethod -Uri "$Base/api/world/wipe" -Method Post -TimeoutSec 120

Write-Host ''
Write-Host '=== 清掉了' -ForegroundColor Cyan
Write-Host ('  角色 {0}、幕 {1}、历史 {2} 条、请求 {3}、场所 {4}' -f `
        $r.cleared.characters, $r.cleared.scenes, $r.cleared.turns, $r.cleared.pleas, $r.cleared.venues)

Write-Host '=== 留下的' -ForegroundColor Cyan
Write-Host ('  {0} 个幕（{1}）、祈愿点 {2}、神 {3}、愿望 {4}' -f `
        $r.left.scenes, $r.left.active, $r.left.shrines, $r.left.deities, $r.left.wishes)
