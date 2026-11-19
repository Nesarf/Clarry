# test-plea.ps1 - 验请求：卡住的东西能说出口、别人看得见、三条出路各走通，
# 「没人应」是作废不是失败，而且它会被记进历史。

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

Write-Host '=== 1. 把卡住的地方说出口'
$plea = Post-J '/api/plea' @{ fromId = 'alice'; blocked = 'object'; needs = '一叠干净的纸'; text = '谁有多余的纸'; lifetime = 3 }
Check '请求开了出来' ($plea.id.Length -gt 0) ("{0} / {1} / {2}" -f $plea.id, $plea.blockedText, $plea.needs)
Check '缺的是东西' ($plea.blocked -eq 'object') $plea.blockedText
Check '默认还剩三轮' ($plea.lifetime -eq 3) ("lifetime {0}" -f $plea.lifetime)

$open = @((Get-J '/api/pleas').open)
Check '别人看得见它在等' (($open | Where-Object { $_.id -eq $plea.id }) -ne $null) ("open {0} 条" -f $open.Count)

$code = Status-Of { Post-J '/api/plea' @{ fromId = 'alice'; blocked = 'nosuch'; needs = 'x' } }
Check '不认识的缺口被拒绝' ($code -eq 400) ("HTTP {0}" -f $code)

Write-Host '=== 2. 自己应不了自己'
$me = @((Get-J '/api/pleas').open) | Where-Object { $_.id -eq $plea.id }
$code = Status-Of { Post-J '/api/plea/answer' @{ pleaId = $me.id; byId = 'alice' } }
Check '提请求的人不能自己应' ($code -eq 409) ("HTTP {0}" -f $code)

Write-Host '=== 3. 另一个人应了'
$answered = Post-J '/api/plea/answer' @{ pleaId = $plea.id; byId = 'carol' }
Check '请求变成了已应' ($answered.state -eq 'answered') ("state {0}" -f $answered.state)
Check '记下了是谁应的' ($answered.answeredBy -eq 'carol') ("answeredBy {0}" -f $answered.answeredBy)

$code = Status-Of { Post-J '/api/plea/answer' @{ pleaId = $plea.id; byId = 'bob' } }
Check '应过的不能再应' ($code -eq 409) ("HTTP {0}" -f $code)

Write-Host '=== 4. 指名的请求，别人插不上手'
$named = Post-J '/api/plea' @{ fromId = 'bob'; blocked = 'skill'; needs = '会辨草药的人'; text = 'carol，你懂这个吗'; toId = 'carol' }
Check '指名给了 carol' ($named.toId -eq 'carol') ("toId {0}" -f $named.toId)

$code = Status-Of { Post-J '/api/plea/answer' @{ pleaId = $named.id; byId = 'alice' } }
Check '没被指名的人应不了' ($code -eq 409) ("HTTP {0}" -f $code)

$taken = Post-J '/api/plea/answer' @{ pleaId = $named.id; byId = 'carol' }
Check '被指名的人应得下来' ($taken.answeredBy -eq 'carol') ("answeredBy {0}" -f $taken.answeredBy)

Write-Host '=== 5. 没人应，就作废——这不是失败'
$lonely = Post-J '/api/plea' @{ fromId = 'alice'; blocked = 'money'; needs = '一笔路费'; text = '有人愿意借我一点吗'; lifetime = 2 }
Check '还剩两轮' ($lonely.lifetime -eq 2) ("lifetime {0}" -f $lonely.lifetime)

for ($i = 0; $i -lt 2; $i++) {
    Post-J '/api/round/begin' $null | Out-Null
    Post-J '/api/round/resolve' $null | Out-Null
}

$board = Get-J '/api/pleas'
$gone = @($board.lapsed) | Where-Object { $_.id -eq $lonely.id }
Check '两轮之后它过期了' ($gone -ne $null) $(if ($gone) { "state {0}" -f $gone.state } else { '还在 open 里' })
Check '过期的请求不再挂在等着的那一栏' ((@($board.open) | Where-Object { $_.id -eq $lonely.id }).Count -eq 0) ("open {0} 条" -f @($board.open).Count)

Write-Host '=== 6. 求与应都进了历史'
$history = Get-J '/api/history?take=60'
$asked = @($history | Where-Object { $_.kind -eq 'ask' })
$said = @($history | Where-Object { $_.kind -eq 'answer' })
Check '开口求人这件事进了历史' ($asked.Count -gt 0) $(if ($asked) { $asked[-1].text } else { '没有' })
Check '应承这件事进了历史' ($said.Count -gt 0) $(if ($said) { $said[-1].text } else { '没有' })

Write-Host '=== 7. 结算里应承会走到提示里'
$fresh = Post-J '/api/plea' @{ fromId = 'bob'; blocked = 'place'; needs = '一处安静的屋子'; text = '谁那儿能借我坐一晚' }
Post-J '/api/round/begin' $null | Out-Null
Post-J '/api/round/order' @{ actorId = 'alice'; kind = 'answer'; pleaId = $fresh.id; targetId = 'bob'; text = '来我这儿' } | Out-Null
# 不能用 | Out-Null，那会把返回值也丢掉。
$resolved = Post-J '/api/round/resolve' $null
$revealed = @($resolved.revealed) -join ' / '
Check '结算把应承说了出来' ($revealed -match '应了') $revealed
Check '请求被标成已应' ((@((Get-J '/api/pleas').answered) | Where-Object { $_.id -eq $fresh.id }) -ne $null) '在已应那一栏'

Write-Host '=== 8. 存档记得住请求'
Post-J '/api/save' $null | Out-Null
$saved = Get-J '/api/pleas'
$total = @($saved.open).Count + @($saved.answered).Count + @($saved.lapsed).Count
Check '三类请求都还在' ($total -ge 4) ("open {0} / answered {1} / lapsed {2}" -f @($saved.open).Count, @($saved.answered).Count, @($saved.lapsed).Count)

Write-Host ''
if ($fails -eq 0) { Write-Host '全部检查通过' -ForegroundColor Green }
else { Write-Host ("{0} 项检查未通过" -f $fails) -ForegroundColor Red; exit 1 }
