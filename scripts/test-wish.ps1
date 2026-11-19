# test-wish.ps1 - 验许愿池：四种愿望各管一件事，一场只有三次，用完就只能看着。
#
# 重点不是「能许愿」，而是「许愿有成本、愿望是成套的、而且真的改变了世界」。

$base = 'http://127.0.0.1:3090'

function Get-J([string]$p) { Invoke-RestMethod -Uri "$base$p" -Method Get -TimeoutSec 30 }
function Post-J([string]$p, $b) {
    if ($null -eq $b) { Invoke-RestMethod -Uri "$base$p" -Method Post -TimeoutSec 30 }
    else {
        Invoke-RestMethod -Uri "$base$p" -Method Post -TimeoutSec 30 `
            -ContentType 'application/json' -Body ($b | ConvertTo-Json -Depth 10)
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

Write-Host '=== 0. 清场，重建三人'
Post-J '/api/loop/stop' $null | Out-Null
Post-J '/api/scene/new?clueGoal=99' $null | Out-Null
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

$w = (Get-J '/api/world').wishes
Check '开场有三次愿望' ($w.left -eq 3) ("{0}/{1}" -f $w.left, $w.budget)

Write-Host '=== 1. 先建立一段羁绊，供之后「让谁忘掉」用'
Post-J '/api/round/begin' $null | Out-Null
Post-J '/api/round/order' @{ actorId = 'alice'; kind = 'give'; targetId = 'bob'; text = '拿着。' } | Out-Null
Post-J '/api/round/resolve' $null | Out-Null
Check '羁绊已建立' ((Who 'alice').a.bond.bob -gt 0) ("alice → bob 温度 {0}" -f (Who 'alice').a.bond.bob)

Write-Host '=== 2. 愿望一：让他们见面（压过互温顺序）'
$before = (Get-J '/api/scene/propose') | ForEach-Object { $_.cast -join '+' }
Write-Host ("  许愿前的排法：{0}" -f ($before -join ' | '))
$r = Post-J '/api/wish' @{ kind = 'bringTogether'; subjectId = 'alice'; targetId = 'carol' }
Check '愿望被记下' ($r.left -eq 2) $r.note
$after = Get-J '/api/scene/propose'
$after | ForEach-Object { '    ' + ($_.cast -join ' + ') + '　' + $_.reason }
$pair = $after | Where-Object { ($_.cast -contains 'alice') -and ($_.cast -contains 'carol') }
Check '这两人被排到同一幕' ($null -ne $pair) $(if ($pair) { $pair.reason } else { '没有同组' })
Check '理由写明了是许愿' ($pair -and $pair.reason -like '*许了愿*') $(if ($pair) { $pair.reason } else { '' })

Write-Host '=== 3. 愿望二：给谁一点火'
Post-J '/api/wish' @{ kind = 'kindle'; subjectId = 'bob' } | Out-Null
Check '目标有了余火' ([bool](Who 'bob').a.ember) ("ember={0}" -f (Who 'bob').a.ember)

Write-Host '=== 4. 愿望三：让谁忘掉'
$coldest = (Who 'alice').a.bond.bob
Post-J '/api/wish' @{ kind = 'forget'; subjectId = 'alice' } | Out-Null
Check '羁绊被拿走' ($null -eq (Who 'alice').a.bond.bob) ("原本温度 {0}，现在 {1}" -f $coldest, (Who 'alice').a.bond.bob)

Write-Host '=== 5. 三次用完，第四次必须被拒'
$w = (Get-J '/api/world').wishes
Check '愿望已经用光' ($w.left -eq 0) ("{0}/{1}" -f $w.left, $w.budget)
$code = 0
try {
    Post-J '/api/wish' @{ kind = 'kindle'; subjectId = 'alice' } | Out-Null
    $code = 200
} catch {
    $code = [int]$_.Exception.Response.StatusCode
}
Check '第四次许愿被拒绝' ($code -eq 409) ("HTTP {0}" -f $code)

Write-Host '=== 6. 开新戏，愿望应当回满'
Post-J '/api/scene/new?clueGoal=3' $null | Out-Null
$w = (Get-J '/api/world').wishes
Check '新戏愿望回满' ($w.left -eq 3) ("{0}/{1}" -f $w.left, $w.budget)
Check '许愿记录已清空' (($w.granted | Measure-Object).Count -eq 0) ("{0} 条" -f ($w.granted | Measure-Object).Count)

Write-Host ''
if ($fails -eq 0) { Write-Host '全部检查通过' -ForegroundColor Green }
else { Write-Host ("{0} 项检查未通过" -f $fails) -ForegroundColor Red; exit 1 }
