# test-lineweight.ps1 - 验镜头层：全场/特写/回忆三种取景由结算结果决定，
# 候选图从最具体排到一定存在的那一张，缺哪张退哪张。

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
function Order([string]$actor, [string]$kind, [string]$target, [string]$text) {
    Post-J '/api/round/order' @{ actorId = $actor; kind = $kind; targetId = $target; text = $text } | Out-Null
}
function Shots {
    $out = @{}
    foreach ($c in (Get-J '/api/cast')) { $out[$c.id] = $c.shot }
    return $out
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

Write-Host '=== 0. 清场，重建三个人'
Post-J '/api/loop/stop' $null | Out-Null
Start-Sleep -Milliseconds 300
Post-J '/api/scene/new?clueGoal=99' $null | Out-Null
foreach ($r in (Get-J '/api/tiers').residents) {
    if ($r.seated) { Post-J '/api/stage/leave' @{ id = $r.id } | Out-Null }
}
foreach ($id in @('alice', 'bob', 'carol')) {
    Post-J '/api/character' @{
        id = $id; name = $id; isPlayer = $false
        sheet  = @{ role = '居民'; traits = @(); goals = @(); fears = @(); secrets = @(); relations = @{} }
        visual = @{ kind = 'none'; expressions = @() }
        agent  = @{ kind = 'llm'; autonomy = 'guided' }
    } | Out-Null
    Post-J '/api/stage/enter' @{ id = $id } | Out-Null
}

Write-Host '=== 1. 平常的轮次是全场'
Post-J '/api/round/begin' $null | Out-Null
Order 'alice' 'speak' '' '今晚风大'
Order 'bob' 'speak' '' '嗯'
Order 'carol' 'speak' '' '关窗吧'
Post-J '/api/round/resolve' $null | Out-Null
$s1 = Shots
Check '三个人都在全场' (($s1['alice'] -eq 'wide') -and ($s1['bob'] -eq 'wide')) ("alice={0} bob={1} carol={2}" -f $s1['alice'], $s1['bob'], $s1['carol'])

Write-Host '=== 2. 说真话的人给特写'
Post-J '/api/round/begin' $null | Out-Null
Order 'alice' 'reveal' '' '我一直都知道'
Order 'bob' 'speak' '' '知道什么'
Order 'carol' 'speak' '' '别说了'
Post-J '/api/round/resolve' $null | Out-Null
$s2 = Shots
Check '说真话的人被推近' ($s2['alice'] -eq 'closeup') ("alice={0}" -f $s2['alice'])
Check '没说话的仍是全场' ($s2['carol'] -eq 'wide') ("carol={0}" -f $s2['carol'])

Write-Host '=== 3. 被看穿的人也给特写'
# 先把人绷紧。探的成败是「洞察 + 对方余下的镇定 + 对方身上的压力」三者相加，
# 50 的洞察盯着一个 52 的镇定、20 的压力，加起来只有 85，够不到 87 的门槛。
Post-J '/api/round/begin' $null | Out-Null
for ($i = 0; $i -lt 20; $i++) { Order 'bob' 'withhold' '' '' }
Post-J '/api/round/resolve' $null | Out-Null

Post-J '/api/round/begin' $null | Out-Null
Order 'alice' 'probe' 'bob' '让我看看'
Order 'alice' 'probe' 'bob' '再看一眼'
Order 'bob' 'speak' '' '没什么'
Post-J '/api/round/resolve' $null | Out-Null

$revealed = (Get-J '/api/history?take=30') | Where-Object { $_.text -match '看穿' }
Check '这一轮确实看穿了' ($revealed.Count -gt 0) $(if ($revealed) { $revealed[-1].text } else { '没有看穿' })

$s3 = Shots
Check '被看穿的人被推近' ($s3['bob'] -eq 'closeup') ("bob={0}" -f $s3['bob'])
Check '看穿别人的人也推近' ($s3['alice'] -eq 'closeup') ("alice={0}" -f $s3['alice'])

Write-Host '=== 4. 失名那一刻是回忆镜头'
Post-J '/api/round/begin' $null | Out-Null
Order 'bob' 'give' 'alice' '拿着这个'
Post-J '/api/round/resolve' $null | Out-Null

# 沉默不花行动点，所以一轮里可以一直沉默，把压力直接推到天花板。
Post-J '/api/round/begin' $null | Out-Null
for ($i = 0; $i -lt 80; $i++) { Order 'bob' 'withhold' '' '' }
Post-J '/api/round/resolve' $null | Out-Null

$hollowed = (Get-J '/api/history?take=40') | Where-Object { $_.text -match '忘了' }
Check '压力到顶，名字被拿走' ($hollowed.Count -gt 0) $(if ($hollowed) { $hollowed[-1].text } else { '没有失名' })

$s4 = Shots
Check '失名的人进了回忆镜头' ($s4['bob'] -eq 'memory') ("bob={0}" -f $s4['bob'])

Write-Host '=== 5. 候选图从最具体排到一定存在的那一张'
$cast = Get-J '/api/cast'
$bobCard = $cast | Where-Object { $_.id -eq 'bob' }
Check '有镜头名' ($bobCard.shotText.Length -gt 0) ("{0} / {1}" -f $bobCard.shot, $bobCard.shotText)
Check '第一张是这一格该用的' ($bobCard.faces[0] -match 'bob-memory\.png$') $bobCard.faces[0]
Check '第二张是线稿' ($bobCard.faces[1] -match 'bob-line-') $bobCard.faces[1]
Check '最后一张是基础的，一定存在' ($bobCard.faces[-1] -match '/bob\.png$') $bobCard.faces[-1]

$flat = $cast | Where-Object { $_.id -eq 'carol' }
Check '全场的人候选里没有镜头图' (($flat.faces | Where-Object { $_ -match 'closeup|memory' }).Count -eq 0) (($flat.faces) -join ' → ')

Write-Host '=== 6. 资产清点脚本能跑，并且说得清缺什么'
$audit = & (Join-Path $PSScriptRoot 'check-art.ps1') *>&1 | Out-String
Check '清点脚本跑通了' ($audit -match '汇总') '有汇总行'
Check '它点出了缺的差分' ($audit -match '缺 .*-closeup\.png') '列出了 closeup 的缺口'

Write-Host ''
if ($fails -eq 0) { Write-Host '全部检查通过' -ForegroundColor Green }
else { Write-Host ("{0} 项检查未通过" -f $fails) -ForegroundColor Red; exit 1 }
