# test-expression.ps1 - 验表情差分：脸由结算结果决定，不是由观众选。
# 说真话/给予→舒展，沉默/行动被丢→沉下来，被人看穿→被点到，带火压过一切。

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

$fails = 0
function Check([string]$label, [bool]$ok, [string]$detail) {
    if ($ok) {
        Write-Host ("  通过  {0}{1}" -f $label, $(if ($detail) { "  （$detail）" } else { '' })) -ForegroundColor Green
    } else {
        Write-Host ("  失败  {0}{1}" -f $label, $(if ($detail) { "  （$detail）" } else { '' })) -ForegroundColor Red
        $script:fails++
    }
}

function Faces {
    $out = @{}
    foreach ($c in (Get-J '/api/cast')) { $out[$c.id] = $c.expression }
    return $out
}

function Order([string]$actor, [string]$kind, [string]$target, [string]$text) {
    Post-J '/api/round/order' @{ actorId = $actor; kind = $kind; targetId = $target; text = $text } | Out-Null
}

Write-Host '=== 0. 清场：清台，重建三个人'
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

$start = Faces
Check '开场时所有人的脸都是平常的' (($start['alice'] -eq 'neutral') -and ($start['bob'] -eq 'neutral')) ("alice={0} bob={1} carol={2}" -f $start['alice'], $start['bob'], $start['carol'])

Write-Host '=== 1. 说真话的人点亮余火，余火压过一切'
Post-J '/api/round/begin' $null | Out-Null
Order 'alice' 'reveal' '' '我其实一直都知道'
Order 'bob' 'withhold' '' ''
Order 'carol' 'give' 'bob' '这个给你'
Post-J '/api/round/resolve' $null | Out-Null

$r1 = Faces
Check '说了真话的人带着火' ($r1['alice'] -eq 'ember') ("alice={0}" -f $r1['alice'])
Check '沉默的人沉下来' ($r1['bob'] -eq 'unhappy') ("bob={0}" -f $r1['bob'])
Check '给出东西的人舒展' ($r1['carol'] -eq 'happy') ("carol={0}" -f $r1['carol'])

Write-Host '=== 2. 火烧尽了，脸回到平常'
Post-J '/api/round/begin' $null | Out-Null
Order 'alice' 'act' '' '推开窗'
Order 'bob' 'speak' '' '外面在下雨'
Order 'carol' 'speak' '' '我知道'
Post-J '/api/round/resolve' $null | Out-Null

$r2 = Faces
Check '火用掉之后不再是带火' ($r2['alice'] -eq 'neutral') ("alice={0}" -f $r2['alice'])
Check '只说话的人脸是平常的' ($r2['bob'] -eq 'neutral') ("bob={0}" -f $r2['bob'])

Write-Host '=== 3. 被人看穿的人，脸上看得出来'
Post-J '/api/round/begin' $null | Out-Null
# 一次不够：reach = insight + (100-poise)/2 + strain/2，50 的洞察探 50 的镇定差一点，
# 所以同一轮里连探两次，第二次才落到 87 的门槛上。
Order 'alice' 'probe' 'bob' '让我看看你在想什么'
Order 'alice' 'probe' 'bob' '再看一眼'
Order 'bob' 'speak' '' '没什么好看的'
Post-J '/api/round/resolve' $null | Out-Null

$revealed = (Get-J '/api/history?take=20') | Where-Object { $_.text -match '看穿' }
Check '这一次真的看穿了' ($revealed.Count -gt 0) $(if ($revealed) { $revealed[-1].text } else { '没有看穿' })

$r3 = Faces
Check '被看穿的人被点到了' ($r3['bob'] -eq 'surprised') ("bob={0}" -f $r3['bob'])
Check '看穿别人的人自己不受影响' ($r3['alice'] -eq 'neutral') ("alice={0}" -f $r3['alice'])

Write-Host '=== 4. 行动点不够被丢掉，也是沉下来'
Post-J '/api/round/begin' $null | Out-Null
Order 'bob' 'act' '' '一'
Order 'bob' 'act' '' '二'
Order 'bob' 'act' '' '三'
Post-J '/api/round/resolve' $null | Out-Null

$r4 = Faces
Check '被丢掉一条的人沉下来' ($r4['bob'] -eq 'unhappy') ("bob={0}（第三条行动超了预算）" -f $r4['bob'])

Write-Host '=== 5. 表情也给了要说的话'
$cast = Get-J '/api/cast'
$bobCard = $cast | Where-Object { $_.id -eq 'bob' }
Check '有表情名' ($bobCard.expressionText.Length -gt 0) ("{0} / {1}" -f $bobCard.expression, $bobCard.expressionText)
Check '有差分的路径可试' ($bobCard.face -match 'bob-unhappy\.png$') $bobCard.face
Check '退回原图的路径一直在' ($bobCard.portrait -match 'bob\.png$') $bobCard.portrait

Write-Host '=== 6. 台下的人不在名单上'
Post-J '/api/stage/leave' @{ id = 'carol' } | Out-Null
$after = Faces
Check '下台的人不再出现' ($null -eq $after['carol']) ("名单：{0}" -f (($after.Keys) -join '、'))

Write-Host ''
if ($fails -eq 0) { Write-Host '全部检查通过' -ForegroundColor Green }
else { Write-Host ("{0} 项检查未通过" -f $fails) -ForegroundColor Red; exit 1 }
