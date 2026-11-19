# test-scene-picker.ps1 - 验「谁和谁凑成一幕」这条规则：按互温配对，同分按 id 排，
# 同一个世界重复调用必须排出同样的幕。这条不确定，生活模拟就不成立。

$base = 'http://127.0.0.1:3090'

function Get-J([string]$p) { Invoke-RestMethod -Uri "$base$p" -Method Get -TimeoutSec 30 }
function Post-J([string]$p, $b) {
    if ($null -eq $b) { Invoke-RestMethod -Uri "$base$p" -Method Post -TimeoutSec 30 }
    else {
        Invoke-RestMethod -Uri "$base$p" -Method Post -TimeoutSec 30 `
            -ContentType 'application/json' -Body ($b | ConvertTo-Json -Depth 10)
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
Post-J '/api/loop/stop' $null | Out-Null
Post-J '/api/scene/new?clueGoal=99' $null | Out-Null
# 别的测试留下的角色还站在台上，会让配对结果多出几对。这里只留本测试建的三个人，
# 并且断言只看这三个人，不看总数。
foreach ($r in (Get-J '/api/tiers').residents) {
    if ($r.seated) { Post-J '/api/stage/leave' @{ id = $r.id } | Out-Null }
}

Write-Host '=== 1. 重建三人，羁绊清零'
$cast = @(
    @{ id = 'alice'; name = '爱丽丝'; role = '剑士' },
    @{ id = 'bob';   name = '鲍勃';   role = '学者' },
    @{ id = 'carol'; name = '卡萝尔'; role = '吟游诗人' }
)
foreach ($c in $cast) {
    Post-J '/api/character' @{
        id = $c.id; name = $c.name; isPlayer = $false
        sheet  = @{ role = $c.role; traits = @(); goals = @(); fears = @(); secrets = @(); relations = @{} }
        visual = @{ kind = 'pixel'; ref = "/assets/portraits/$($c.id).png"; expressions = @() }
        agent  = @{ kind = 'llm'; autonomy = 'guided' }
    } | Out-Null
    Post-J '/api/stage/enter' @{ id = $c.id } | Out-Null
}
$bonds = (Get-J '/api/cast') | ForEach-Object { $_.a.bond.PSObject.Properties.Name.Count }
Check '羁绊已清空' (($bonds | Measure-Object -Sum).Sum -eq 0) ("羁绊项合计 {0}" -f (($bonds | Measure-Object -Sum).Sum))

Write-Host '=== 2. 全是冷的时候：按 id 序配，剩一个单独一幕'
$p1 = Get-J '/api/scene/propose'
Write-Host ("  排出 {0} 幕" -f $p1.Count)
$p1 | ForEach-Object { '    ' + ($_.cast -join ' + ') + '　' + $_.reason }
Check '总共排出两幕' ($p1.Count -eq 2) ("{0} 幕" -f $p1.Count)
Check '有一幕是单人' (($p1 | Where-Object { $_.solo }).Count -eq 1) ''

Write-Host '=== 3. 确定性：连着问两次，答案必须一样'
$p2 = Get-J '/api/scene/propose'
$a = ($p1 | ForEach-Object { $_.cast -join '+' }) -join ' | '
$b = ($p2 | ForEach-Object { $_.cast -join '+' }) -join ' | '
Check '两次排出同样的幕' ($a -eq $b) $a

Write-Host '=== 4. 让爱丽丝与卡萝尔热起来，她们应当被排到一起'
Post-J '/api/round/begin' $null | Out-Null
Post-J '/api/round/order' @{ actorId = 'alice'; kind = 'give'; targetId = 'carol'; text = '这个给你。' } | Out-Null
Post-J '/api/round/order' @{ actorId = 'carol'; kind = 'give'; targetId = 'alice'; text = '那我也回一个。' } | Out-Null
Post-J '/api/round/resolve' $null | Out-Null

$p3 = Get-J '/api/scene/propose'
$p3 | ForEach-Object { '    ' + ($_.cast -join ' + ') + '　' + $_.reason }
$together = $p3 | Where-Object { ($_.cast -contains 'alice') -and ($_.cast -contains 'carol') }
Check '爱丽丝与卡萝尔被排到同一幕' ($null -ne $together) $(if ($together) { $together.reason } else { '没有同组' })
Check '最热的那一对排在第一个' ($p3[0].cast -contains 'alice' -and $p3[0].cast -contains 'carol') ($p3[0].cast -join ' + ')

Write-Host '=== 5. 单独的人仍然拿得到自己那一幕'
$solo = $p3 | Where-Object { $_.solo }
Check '还有一幕是单人' ($null -ne $solo) $(if ($solo) { $solo.reason } else { '没有单人幕' })

Write-Host ''
if ($fails -eq 0) { Write-Host '全部检查通过' -ForegroundColor Green }
else { Write-Host ("{0} 项检查未通过" -f $fails) -ForegroundColor Red; exit 1 }
