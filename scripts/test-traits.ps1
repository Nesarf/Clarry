# test-traits.ps1 - 验性格会变：傲娇不会消失，只会变轻；坦率从零长出来。
#
# 重点不是「数值变了」，而是「旧的那条还在」。若傲娇被替换成坦率，这个角色就变成了
# 另一个角色，而不是同一个人变得更通达。

$base = 'http://127.0.0.1:3090'

function Get-J([string]$p) { Invoke-RestMethod -Uri "$base$p" -Method Get -TimeoutSec 30 }
function Post-J([string]$p, $b) {
    if ($null -eq $b) { Invoke-RestMethod -Uri "$base$p" -Method Post -TimeoutSec 30 }
    else {
        Invoke-RestMethod -Uri "$base$p" -Method Post -TimeoutSec 30 `
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

Write-Host '=== 0. 清场，建一个傲娇的人'
Post-J '/api/loop/stop' $null | Out-Null
Post-J '/api/scene/new?clueGoal=99' $null | Out-Null
Post-J '/api/character' @{
    id = 'alice'; name = '爱丽丝'; isPlayer = $false
    sheet = @{
        role = '剑士'; summary = '嘴上不饶人的年轻剑士。'
        traits = @('傲娇', '不擅长说谎')
        goals = @(); fears = @(); secrets = @(); relations = @{}
    }
    visual = @{ kind = 'pixel'; ref = '/assets/portraits/alice.png'; expressions = @() }
    agent  = @{ kind = 'llm'; autonomy = 'guided' }
} | Out-Null
Post-J '/api/stage/enter' @{ id = 'alice' } | Out-Null
Post-J '/api/stage/enter' @{ id = 'bob' } | Out-Null

$start = Who 'alice'
Write-Host ("  开场：{0}" -f $start.traitsShowing)
Check '开场是傲娇' ($start.traitsNow.傲娇 -eq 60) ("傲娇 {0}" -f $start.traitsNow.傲娇)
Check '还没有坦率' ($null -eq $start.traitsNow.坦率) ''

Write-Host '=== 1. 连着说五次真话'
for ($i = 1; $i -le 5; $i++) {
    Post-J '/api/round/begin' $null | Out-Null
    Post-J '/api/round/order' @{ actorId = 'alice'; kind = 'reveal'; text = "有些话我其实一直想说（第 $i 次）" } | Out-Null
    Post-J '/api/round/resolve' $null | Out-Null
}

$after = Who 'alice'
Write-Host ("  五次之后：{0}" -f $after.traitsShowing)
Check '坦率长出来了' ($after.traitsNow.坦率 -gt 0) ("坦率 {0}" -f $after.traitsNow.坦率)
Check '坦率在往上走' ($after.traitsNow.坦率 -ge 35) ("坦率 {0}" -f $after.traitsNow.坦率)
Check '傲娇变轻了' ($after.traitsNow.傲娇 -lt 60) ("60 → {0}" -f $after.traitsNow.傲娇)
Check '傲娇没有消失' ($after.traitsNow.傲娇 -gt 0) ("还剩 {0}" -f $after.traitsNow.傲娇)
Check '两条同时都在' (($null -ne $after.traitsNow.傲娇) -and ($null -ne $after.traitsNow.坦率)) $after.traitsShowing

Write-Host '=== 2. 给出去的东西多了，会长出心软'
Post-J '/api/round/begin' $null | Out-Null
Post-J '/api/round/order' @{ actorId = 'alice'; kind = 'give'; targetId = 'bob'; text = '拿着，别误会' } | Out-Null
Post-J '/api/round/resolve' $null | Out-Null
$g = Who 'alice'
Check '心软出现了' ($g.traitsNow.心软 -gt 0) ("心软 {0}" -f $g.traitsNow.心软)

Write-Host '=== 3. 沉默多了会转向内敛，而不是同一条路'
Post-J '/api/round/begin' $null | Out-Null
Post-J '/api/round/order' @{ actorId = 'alice'; kind = 'withhold'; text = '……' } | Out-Null
Post-J '/api/round/resolve' $null | Out-Null
$w = Who 'alice'
Check '内敛也出现了' ($w.traitsNow.内敛 -gt 0) ("内敛 {0}" -f $w.traitsNow.内敛)

Write-Host '=== 4. 性格已经写进给模型看的那一段'
$now = Who 'alice'
Check '展示串里有分量' ($now.traitsShowing -match '\d+') $now.traitsShowing

Write-Host ''
if ($fails -eq 0) { Write-Host '全部检查通过' -ForegroundColor Green }
else { Write-Host ("{0} 项检查未通过" -f $fails) -ForegroundColor Red; exit 1 }
