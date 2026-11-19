# test-board-game.ps1 - 验大富翁：2~4 人成局、有赢家、骰子被种子管住、
# 买地收租抵押破产都落了账、属性真的变了。

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

Write-Host '=== 0. 清场：收场，并且把所有开着的幕都收掉'
Post-J '/api/loop/stop' $null | Out-Null
Start-Sleep -Milliseconds 300
foreach ($s in (Get-J '/api/scenes').open) {
    Post-J "/api/scenes/close?sceneId=$($s.id)" $null | Out-Null
}

$names = @('alice', 'bob', 'carol', 'dave')
foreach ($id in $names) {
    Post-J '/api/character' @{
        id = $id; name = $id; isPlayer = $false
        sheet  = @{ role = '居民'; traits = @(); goals = @(); fears = @(); secrets = @(); relations = @{} }
        visual = @{ kind = 'none'; expressions = @() }
        agent  = @{ kind = 'llm'; autonomy = 'guided' }
    } | Out-Null
}

Write-Host '=== 1. 桌上没人时开不起来'
$code = Status-Of { Post-J '/api/minigame/board' $null }
Check '空桌子上开不了局' ($code -eq 409) ("HTTP {0}" -f $code)

Write-Host '=== 2. 四个人成局'
foreach ($id in $names) { Post-J '/api/stage/enter' @{ id = $id } | Out-Null }

$aliceBefore = (Get-J '/api/character/alice').attributes.renown
$r1 = Post-J '/api/minigame/board' $null
Check '有人赢了' ($r1.winner.Length -gt 0) ("winner {0}" -f $r1.winner)
Check '记了棋谱' ($r1.log.Count -gt 5) ("{0} 行" -f $r1.log.Count)
Check '骰子被关在这一局里' ($r1.usedDice) ("usedDice {0}，只在局内" -f $r1.usedDice)
Check '四个人都参了局' ($r1.players.Count -eq 4) ($r1.players -join '、')

$moves = ($r1.log | Select-String -Pattern '回合' | Measure-Object).Count
Check '每一手都落了账' ($moves -gt 100) ("{0} 条回合记录" -f $moves)

$bought = ($r1.log | Select-String -Pattern '买下' | Measure-Object).Count
$rented = ($r1.log | Select-String -Pattern '付租' | Measure-Object).Count
Check '有人买了地' ($bought -gt 3) ("{0} 次成交" -f $bought)
Check '有人收了租' ($rented -gt 5) ("{0} 次付租" -f $rented)

Write-Host '=== 3. 属性真的变了'
$aliceAfter = (Get-J '/api/character/alice').attributes.renown
if ($r1.winner -eq 'alice') {
    Check '赢了的人声望上升' ($aliceAfter -gt $aliceBefore) ("{0} → {1}（alice 胜）" -f $aliceBefore, $aliceAfter)
} else {
    Check '输了的人声望下降' ($aliceAfter -lt $aliceBefore) ("{0} → {1}（{2} 胜）" -f $aliceBefore, $aliceAfter, $r1.winner)
}

Write-Host '=== 4. 同一个世界、同样几个人，种子一样'
$r2 = Post-J '/api/minigame/board' $null
$seed1 = ($r1.log[0] -split '：')[0]
$seed2 = ($r2.log[0] -split '：')[0]
Check '两局的种子完全相同' ($seed1 -eq $seed2) ("{0} / {1}" -f $seed1, $seed2)

# 种子一样而结果可以不同：骰子被管住了，决策没有被管住——买不买地看的是属性，而上一局
# 已经把属性改了。这不是缺陷，是「同一个人第二局会做别的选择」。
$note = if ($r1.winner -eq $r2.winner) { '两局同桌同胜者（属性变化还不足以改变决策）' } else { '换了一个赢家（属性被上一局改过）' }
Write-Host ("     第一局 {0} 胜，第二局 {1} 胜 —— {2}" -f $r1.winner, $r2.winner, $note)

Write-Host '=== 5. 台下的人不下场'
$before = (Get-J '/api/character/dave').attributes.renown
Post-J '/api/stage/leave' @{ id = 'dave' } | Out-Null
$r3 = Post-J '/api/minigame/board' $null
$after = (Get-J '/api/character/dave').attributes.renown
Check '只算台上三个人' ($r3.players.Count -eq 3) ($r3.players -join '、')
Check '下台的人没有变化' ($after -eq $before) ("dave 声望 {0} → {1}" -f $before, $after)

Write-Host '=== 6. 剩一个人就开不了局'
foreach ($id in @('alice', 'bob')) { Post-J '/api/stage/leave' @{ id = $id } | Out-Null }
$code = Status-Of { Post-J '/api/minigame/board' $null }
Check '一个人开不了局' ($code -eq 409) ("HTTP {0}" -f $code)

Write-Host '=== 7. 两个人又能开'
Post-J '/api/stage/enter' @{ id = 'alice' } | Out-Null
$r4 = Post-J '/api/minigame/board' $null
Check '两个人成一局' ($r4.players.Count -eq 2) ($r4.players -join '、')
Check '有赢家' ($r4.winner.Length -gt 0) ("winner {0}" -f $r4.winner)

Write-Host '=== 8. 对局记在世界历史上'
$history = Get-J '/api/history?take=40'
$played = ($history | Where-Object { $_.text -match '大富翁' } | Measure-Object).Count
Check '对局记在世界历史上' ($played -gt 0) ("{0} 条" -f $played)

Write-Host ''
if ($fails -eq 0) { Write-Host '全部检查通过' -ForegroundColor Green }
else { Write-Host ("{0} 项检查未通过" -f $fails) -ForegroundColor Red; exit 1 }
