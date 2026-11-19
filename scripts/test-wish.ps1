# test-wish.ps1 - 验许愿池降级之后的样子：只剩三种愿望，一场两次。
#
# 这套测试以前验的是四种干预：让他们见面、给谁点火、让谁忘掉、叫停。前三种现在砍掉了,
# 因为那都是 agent 自己能做的事——配对器会找人、Reveal 会点火、hollowing 会遗忘。
# 所以这一套要验的不只是「还能许愿」，还有「砍掉的那三种根本不被认识」。

$base = 'http://127.0.0.1:3090'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [System.Text.Encoding]::UTF8

function Get-J([string]$p) { Invoke-RestMethod -Uri "$base$p" -Method Get -TimeoutSec 30 }
function Post-J([string]$p, $b) {
    if ($null -eq $b) { Invoke-RestMethod -Uri "$base$p" -Method Post -TimeoutSec 30 }
    else {
        Invoke-RestMethod -Uri "$base$p" -Method Post -TimeoutSec 30 `
            -ContentType 'application/json' -Body ($b | ConvertTo-Json -Depth 10)
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
Check '开场有两次愿望' ($w.left -eq 2) ("{0}/{1}" -f $w.left, $w.budget)

Write-Host '=== 1. 砍掉的三种，现在根本不认识'
foreach ($gone in @('bringTogether', 'kindle', 'forget')) {
    $code = Status-Of { Post-J '/api/wish' @{ kind = $gone; subjectId = 'alice'; targetId = 'bob' } }
    Check ("「{0}」被拒绝" -f $gone) ($code -eq 400) ("HTTP {0}" -f $code)
}
$w = (Get-J '/api/world').wishes
Check '被拒绝的愿望不占配额' ($w.left -eq 2) ("{0}/{1}" -f $w.left, $w.budget)

Write-Host '=== 2. 镜头：不改变任何东西的愿望'
$scene = (Get-J '/api/scenes').open | Where-Object { $_.cast -contains 'alice' }
$focused = Post-J '/api/wish' @{ kind = 'focus'; subjectId = $scene.id }
Check '镜头挪过去了' ($focused.left -eq 1) ("剩 {0} 次" -f $focused.left)
$aud = Get-J '/api/audience'
Check '观众视角跟着它' ($aud.watching -eq $scene.id) ("在看 {0}" -f $aud.watching)

Write-Host '=== 3. 应一条请求：唯一还能改变事情的愿望'
$plea = Post-J '/api/plea' @{
    fromId = 'bob'; blocked = 'object'; needs = '一本讲雾的书'; text = '石阶前留了张纸'
}
$granted = Post-J '/api/wish' @{ kind = 'grant'; subjectId = $plea.id }
Check '请求被应了' ($granted.left -eq 0) ("剩 {0} 次" -f $granted.left)
Check '记下的是神应的' ($granted.note -match '应了 bob 的请求') $granted.note

$answered = @((Get-J '/api/pleas').answered) | Where-Object { $_.id -eq $plea.id }
Check '请求变成了已应' ($answered -ne $null) $(if ($answered) { "answeredBy {0}" -f $answered.answeredBy } else { '没找到' })

Write-Host '=== 4. 两次用完，第三次必须被拒'
$code = Status-Of { Post-J '/api/wish' @{ kind = 'halt' } }
Check '第三次许愿被拒绝' ($code -eq 409) ("HTTP {0}" -f $code)

Write-Host '=== 5. 应一个已经过期的请求也不行'
Post-J '/api/scene/new?clueGoal=99' $null | Out-Null
$stale = Post-J '/api/plea' @{ fromId = 'alice'; blocked = 'money'; needs = '路费'; text = '求一点钱'; lifetime = 1 }
Post-J '/api/round/begin' $null | Out-Null
Post-J '/api/round/resolve' $null | Out-Null

$code = Status-Of { Post-J '/api/wish' @{ kind = 'grant'; subjectId = $stale.id } }
Check '过期的请求应不了' ($code -eq 409) ("HTTP {0}" -f $code)

Write-Host '=== 6. 开新戏，愿望回满'
Post-J '/api/scene/new?clueGoal=3' $null | Out-Null
$w = (Get-J '/api/world').wishes
Check '新戏愿望回满' ($w.left -eq 2) ("{0}/{1}" -f $w.left, $w.budget)
Check '许愿记录已清空' (($w.granted | Measure-Object).Count -eq 0) ("{0} 条" -f ($w.granted | Measure-Object).Count)

Write-Host '=== 7. 叫停仍然在'
$halted = Post-J '/api/wish' @{ kind = 'halt' }
Check '叫停还是观众的权利' ($halted.left -eq 1) ("剩 {0} 次" -f $halted.left)

Write-Host ''
if ($fails -eq 0) { Write-Host '全部检查通过' -ForegroundColor Green }
else { Write-Host ("{0} 项检查未通过" -f $fails) -ForegroundColor Red; exit 1 }
