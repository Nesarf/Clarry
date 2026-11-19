# test-memory.ps1 - 验记忆：它是每个人的，按事情分量不同，而且记性由人格决定。
#
# 这三条只要有一条不成立，记忆就退化成「全体共享的最近若干条」——那正是现在这套要替掉的东西。

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

Write-Host '=== 0. 清场与清记忆'
Post-J '/api/loop/stop' $null | Out-Null
Post-J '/api/scene/new?clueGoal=99' $null | Out-Null
$cleared = Post-J '/api/memory/clear' $null
Write-Host ("  清掉 {0} 行" -f $cleared.rows)

Write-Host '=== 1. 三个记性不同的人'
$cast = @(
    @{ id = 'alice'; name = '爱丽丝'; role = '剑士';     traits = @('谨慎', '好奇') },
    @{ id = 'bob';   name = '鲍勃';   role = '学者';     traits = @('健忘', '粗心') },
    @{ id = 'carol'; name = '卡萝尔'; role = '吟游诗人'; traits = @('敏感', '敏锐') }
)
foreach ($c in $cast) {
    Post-J '/api/character' @{
        id = $c.id; name = $c.name; isPlayer = $false
        sheet  = @{ role = $c.role; traits = $c.traits; goals = @(); fears = @(); secrets = @(); relations = @{} }
        visual = @{ kind = 'none'; expressions = @() }
        agent  = @{ kind = 'llm'; autonomy = 'guided' }
    } | Out-Null
    Post-J '/api/stage/enter' @{ id = $c.id } | Out-Null
}

Write-Host '=== 2. 记性参数应当因人而异'
$a = Get-J '/api/memory/alice'
$b = Get-J '/api/memory/bob'
$c = Get-J '/api/memory/carol'
Check '谨慎的人掉得慢' ($a.style.decay -gt $b.style.decay) ("alice {0} / bob {1}" -f $a.style.decay, $b.style.decay)
Check '敏感的人记得更重' ($c.style.salience -gt $a.style.salience) ("carol {0} / alice {1}" -f $c.style.salience, $a.style.salience)
Check '健忘的人容量更小' ($b.style.capacity -lt $a.style.capacity) ("bob {0} / alice {1}" -f $b.style.capacity, $a.style.capacity)

Write-Host '=== 3. 一轮里发生三件分量不同的事'
Post-J '/api/round/begin' $null | Out-Null
Post-J '/api/round/order' @{ actorId = 'carol'; kind = 'reveal'; text = '那封信是我烧的' } | Out-Null
Post-J '/api/round/order' @{ actorId = 'alice'; kind = 'give'; targetId = 'bob'; text = '拿着，别弄丢' } | Out-Null
Post-J '/api/round/order' @{ actorId = 'bob'; kind = 'speak'; text = '……嗯' } | Out-Null
Post-J '/api/round/resolve' $null | Out-Null

$a = Get-J '/api/memory/alice'
$b = Get-J '/api/memory/bob'
$c = Get-J '/api/memory/carol'

Write-Host '  爱丽丝记得：'
$a.kept | Select-Object -First 3 | ForEach-Object { '    {0}  重 {1}' -f $_.text, $_.weight }
Write-Host '  鲍勃记得：'
$b.kept | Select-Object -First 3 | ForEach-Object { '    {0}  重 {1}' -f $_.text, $_.weight }
Write-Host '  卡萝尔记得：'
$c.kept | Select-Object -First 3 | ForEach-Object { '    {0}  重 {1}' -f $_.text, $_.weight }

Check '爱丽丝记得自己给过东西' (($a.kept | Where-Object { $_.aboutId -eq 'bob' }).Count -gt 0) ''
Check '鲍勃记得别人对他做过什么' (($b.kept | Where-Object { $_.aboutId -eq 'alice' }).Count -gt 0) ''
Check '说出真相的人记得最重' (($c.kept[0].weight) -gt ($a.kept[0].weight)) ("carol {0} / alice {1}" -f $c.kept[0].weight, $a.kept[0].weight)
Check '轻描淡写的话最轻' (($b.kept | Where-Object { $_.text -like '*说话*' } | Select-Object -First 1).weight -lt ($a.kept[0].weight)) ''

Write-Host '=== 4. 记忆是各自的，不是全场的'
$shared = ($a.kept | Where-Object { $_.text -like '*carol*' }).Count
Check '爱丽丝没有直接记住卡萝尔那句话的内容' ($shared -eq 0) ("关于 carol 的条目 {0} 条" -f $shared)

Write-Host '=== 5. 衰减：再跑一轮，旧事应当变轻'
$before = ($a.kept | Select-Object -First 1).weight
Post-J '/api/round/begin' $null | Out-Null
Post-J '/api/round/order' @{ actorId = 'alice'; kind = 'speak'; text = '天亮了' } | Out-Null
Post-J '/api/round/resolve' $null | Out-Null
$a2 = Get-J '/api/memory/alice'
$afterGift = ($a2.kept | Where-Object { $_.aboutId -eq 'bob' } | Select-Object -First 1).weight
Check '旧事的分量下降了' ($afterGift -lt $before) ("{0} → {1}" -f $before, $afterGift)

Write-Host ''
if ($fails -eq 0) { Write-Host '全部检查通过' -ForegroundColor Green }
else { Write-Host ("{0} 项检查未通过" -f $fails) -ForegroundColor Red; exit 1 }
