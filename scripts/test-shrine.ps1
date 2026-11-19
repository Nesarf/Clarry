# test-shrine.ps1 - 验祈愿点与神明：请求写在一块具体的地方，应它的是某位有名有姓的神，
# 世界说出口的那句话里没有观众，也没有人能证实那是谁做的。

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

Write-Host '=== 0. 建一个人，让他站到台上'
Post-J '/api/loop/stop' $null | Out-Null
Start-Sleep -Milliseconds 300
Post-J '/api/scene/new?clueGoal=99' $null | Out-Null
foreach ($r in (Get-J '/api/tiers').residents) {
    if ($r.seated) { Post-J '/api/stage/leave' @{ id = $r.id } | Out-Null }
}
foreach ($id in @('alice', 'bob')) {
    Post-J '/api/character' @{
        id = $id; name = $id; isPlayer = $false
        sheet  = @{ role = '居民'; traits = @(); goals = @(); fears = @(); secrets = @(); relations = @{} }
        visual = @{ kind = 'none'; expressions = @() }
        agent  = @{ kind = 'llm'; autonomy = 'guided' }
    } | Out-Null
    Post-J '/api/stage/enter' @{ id = $id } | Out-Null
}

Write-Host '=== 1. 新世界自带一处可以求人帮忙的地方'
$board = Get-J '/api/shrines'
$first = @($board.shrines)[0]
Check '有一处祈愿点' ($null -ne $first) $(if ($first) { "{0} / {1}" -f $first.name, $first.deityName } else { '没有' })
Check '那块地方上面有位神' ($first.deityName.Length -gt 0) ("{0}，司掌{1}" -f $first.deityName, $first.domain)
Check '神有脾性' ($first.temper.Length -gt 0) $first.temper

Write-Host '=== 2. 请求落在具体的地方'
$plea = Post-J '/api/plea' @{
    fromId = 'alice'; blocked = 'object'; needs = '一盏还亮着的灯'
    text = '石阶前留了张纸'; shrineId = $first.id
}
Check '请求记下了它写在哪' ($plea.id.Length -gt 0) ("{0} → {1}" -f $plea.id, $first.name)

$afterAsk = @((Get-J '/api/shrines').shrines) | Where-Object { $_.id -eq $first.id }
Check '求过一次就记一次' ($afterAsk.asked -ge 1) ("asked {0} / answered {1}" -f $afterAsk.asked, $afterAsk.answered)

Write-Host '=== 3. 神应了它'
$before = (@((Get-J '/api/shrines').shrines) | Where-Object { $_.id -eq $first.id })
$grant = Post-J '/api/plea/grant' @{ pleaId = $plea.id }
Check '应它的是那一位' ($grant.deity -eq $first.deityName) ("{0}（{1}）" -f $grant.deity, $grant.shrine)
Check '神应过的次数涨了' ($grant.answered -eq ($before.answered + 1)) ("{0} → {1}" -f $before.answered, $grant.answered)

$board2 = Get-J '/api/pleas'
$settled = (@($board2.answered) | Where-Object { $_.id -eq $plea.id })
Check '请求变成了已应' ($settled -ne $null) $(if ($settled) { "answeredBy {0}" -f $settled.answeredBy } else { '没找到' })

Write-Host '=== 4. 世界说出口的那句话里没有观众'
$line = $grant.line
Check '写的是东西出现了' ($line -match '出现在') $line
Check '写的是谁应的，用「有人说」' ($line -match '有人说那是' -and $line -match [regex]::Escape($first.deityName)) 'rumour, not fact'
foreach ($word in @('观众', '玩家', '用户', 'Clarry')) {
    Check ("那句话里没有「{0}」" -f $word) ($line -notmatch $word) ''
}

$history = Get-J '/api/history?take=40'
$narrated = @($history | Where-Object { $_.text -match '有人说那是' })
Check '这句话进了历史' ($narrated.Count -gt 0) $(if ($narrated) { $narrated[-1].text } else { '没有' })

Write-Host '=== 5. 应过的不能再应'
$code = Status-Of { Post-J '/api/plea/grant' @{ pleaId = $plea.id } }
Check '同一条请求不能应两次' ($code -eq 409) ("HTTP {0}" -f $code)

Write-Host '=== 6. 能再立一处，挂着另一位神'
# 名字带时间戳：这套测试会被反复跑，撞名是它自己的问题，不是世界的问题。
$well = '后巷的旧井-' + (Get-Date -Format 'HHmmss')
$new = Post-J '/api/shrine' @{ name = $well; deityName = '井下那位'; domain = '账目与欠债'; temper = '要价，但守约' }
Check '新的地方立起来了' ($new.name -eq $well) ("{0} / {1}" -f $new.name, $new.deityName)
Check '它有自己司掌的东西' ($new.domain -eq '账目与欠债') $new.domain

$code = Status-Of { Post-J '/api/shrine' @{ name = $well } }
Check '同名的地方不能立两处' ($code -eq 409) ("HTTP {0}" -f $code)

$second = Post-J '/api/plea' @{
    fromId = 'bob'; blocked = 'money'; needs = '三个铜板'
    text = '井边留了张纸'; shrineId = $new.shrine
}
$granted2 = Post-J '/api/plea/grant' @{ pleaId = $second.id }
Check '第二位神也应得了' ($granted2.deity -eq '井下那位') ("{0} 应了 {1}" -f $granted2.deity, $second.id)

Write-Host '=== 7. prompt 里说的是地方与神，不是镜头后面的人'
$prompt = Post-J '/api/driver/prompt' @{ id = 'alice' }
Check '列出了可以求人的地方' ($prompt.text -match 'Places where a request can be left') '有这一段'
Check '点名了那位神' ($prompt.text -match '雾中那位') '神的名字在'
Check '写清了应不应不由它决定' ($prompt.text -match 'Whether it answers is not yours to know') '有那句'
foreach ($word in @('audience', 'player', 'The sky is watched')) {
    Check ("prompt 里没有「{0}」" -f $word) ($prompt.text -notmatch [regex]::Escape($word)) ''
}

Write-Host '=== 8. 存档记得住地方与神'
Post-J '/api/save' $null | Out-Null
$saved = Get-J '/api/shrines'
Check '两处地方都还在' (@($saved.shrines).Count -ge 2) ("{0} 处" -f @($saved.shrines).Count)
Check '新立的那处还挂着它的神' ((@($saved.shrines) | Where-Object { $_.name -eq $well }).deityName -eq '井下那位') '在'

Write-Host ''
if ($fails -eq 0) { Write-Host '全部检查通过' -ForegroundColor Green }
else { Write-Host ("{0} 项检查未通过" -f $fails) -ForegroundColor Red; exit 1 }
