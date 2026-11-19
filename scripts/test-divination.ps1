# test-divination.ps1 - 验三层持有：随身的东西、基础建筑自带的后院、建在后院里的扩展建筑。
# 四样齐备解锁占星术士，熟练度到线才收费，报出来的天就是天。

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

Write-Host '=== 0. 收场，建两个人（重建即清空上一轮的持有）'
Post-J '/api/loop/stop' $null | Out-Null
Start-Sleep -Milliseconds 300
foreach ($id in @('sage', 'bob')) {
    Post-J '/api/character' @{
        id = $id; name = $id; isPlayer = $false
        sheet  = @{ role = '居民'; traits = @(); goals = @(); fears = @(); secrets = @(); relations = @{} }
        visual = @{ kind = 'none'; expressions = @() }
        agent  = @{ kind = 'llm'; autonomy = 'guided' }
    } | Out-Null
}
$fresh = Get-J '/api/holdings/sage'
Check '重建之后手上什么都没有' ($fresh.carried.Count -eq 0 -and $fresh.buildings.Count -eq 0) ("随身 {0}，建筑 {1}" -f $fresh.carried.Count, $fresh.buildings.Count)

Write-Host '=== 1. 塔罗牌与预言水晶是随身的'
$tarot = Post-J '/api/carry' @{ id = 'sage'; kind = 'tarot'; from = '祖传' }
Check '塔罗牌随身' ($tarot.housing -eq 'carried') ("{0} / {1}" -f $tarot.label, $tarot.housingLabel)
$crystal = Post-J '/api/carry' @{ id = 'sage'; kind = 'crystal' }
Check '预言水晶随身' ($crystal.housing -eq 'carried') ("{0} / {1}" -f $crystal.label, $crystal.housingLabel)

$code = Status-Of { Post-J '/api/carry' @{ id = 'sage'; kind = 'alembic' } }
Check '建筑不能揣在兜里' ($code -eq 409) ("HTTP {0}" -f $code)

Write-Host '=== 2. 炼金工房是基础建筑，自带后院这个区域'
$alembic = Post-J '/api/build' @{ id = 'sage'; kind = 'alembic' }
Check '炼金工房是基础建筑' ($alembic.housing -eq 'basic') ("{0} / {1}" -f $alembic.label, $alembic.housingLabel)
Check '它自带一块区域' ($alembic.plots.Count -eq 1) ("{0} 块：{1}" -f $alembic.plots.Count, (($alembic.plots | ForEach-Object { $_.label }) -join '、'))
Check '那块区域是后院' ($alembic.plots[0].label -eq '后院') ("{0}" -f $alembic.plots[0].label)
$backyard = $alembic.plots[0].id
Check '基础建筑自己就立着' ($alembic.standing) ("standing {0}" -f $alembic.standing)

Write-Host '=== 3. 天文台是扩展建筑，只能建在区域里'
$code = Status-Of { Post-J '/api/build' @{ id = 'sage'; kind = 'observatory' } }
Check '不说建在哪，就建不起来' ($code -eq 409) ("HTTP {0}" -f $code)

$code = Status-Of { Post-J '/api/build' @{ id = 'sage'; kind = 'observatory'; onPlot = 'plot-nosuch' } }
Check '建在不存在的区域上被拒绝' ($code -eq 409) ("HTTP {0}" -f $code)

$code = Status-Of { Post-J '/api/build' @{ id = 'sage'; kind = 'observatory'; onPlot = $tarot.carried } }
Check '拿随身物当场地被拒绝' ($code -eq 409) ("HTTP {0}" -f $code)

$obs = Post-J '/api/build' @{ id = 'sage'; kind = 'observatory'; onPlot = $backyard }
Check '建在炼金工房的后院里，成功了' ($obs.housing -eq 'extension') ("{0} / {1}" -f $obs.label, $obs.housingLabel)
Check '扩建记下了它站在哪块区域里' ($obs.onPlot -eq $backyard) ("onPlot {0}" -f $obs.onPlot)
Check '扩建自己不带场地' ($obs.plots.Count -eq 0) ("plots {0}" -f $obs.plots.Count)

$code = Status-Of { Post-J '/api/build' @{ id = 'sage'; kind = 'observatory'; onPlot = $backyard } }
Check '同样一座建筑不能建两次' ($code -eq 409) ("HTTP {0}" -f $code)

Write-Host '=== 4. 四样齐备才算占星术士'
$state = Get-J '/api/holdings/sage'
Check '随身两件、建筑两座' (($state.carried.Count -eq 2) -and ($state.buildings.Count -eq 2)) ("随身 {0}，建筑 {1}" -f $state.carried.Count, $state.buildings.Count)
Check '没有缺的' ($state.missing.Count -eq 0) (($state.missing) -join '、')
Check '两座建筑都立着' (($state.buildings | Where-Object { -not $_.standing }).Count -eq 0) (($state.buildings | ForEach-Object { "{0}={1}" -f $_.label, $_.standing }) -join '、')
Check '解锁了占星术士' ($state.astrologer) 'astrologer=True'

$sky0 = Get-J '/api/sky'
Write-Host ("     现在的天：{0}" -f $sky0.text)

Write-Host '=== 5. 占卜一次，熟练度涨一点'
$before = (Get-J '/api/holdings/sage').proficiency
$reading = Post-J '/api/divine' @{ byId = 'sage'; kind = 'tarot'; targetId = 'bob'; question = '他今晚会来吗' }
Check '塔罗牌读出了东西' ($reading.reading.Length -gt 0) $reading.reading
Check '熟练度涨了一点' ($reading.proficiency -eq ($before + 1)) ("{0} → {1}" -f $before, $reading.proficiency)

Write-Host '=== 6. 天象：报什么天就是什么天'
$named = Post-J '/api/divine' @{ byId = 'sage'; kind = 'observatory'; question = '今天会下雨吗' }
$sky1 = Get-J '/api/sky'
Check '天按术士说的变了' ($sky1.weather -eq '雨') ("预报 {0}，实际 {1}" -f $named.weather, $sky1.weather)
Check '天象记下了是谁报的' ($sky1.forecastBy -eq 'sage') ("forecastBy {0}" -f $sky1.forecastBy)
Check '夜色随看天变深' ($sky1.night -gt $sky0.night) ("夜色 {0} → {1}" -f $sky0.night, $sky1.night)

Write-Host '=== 7. 没到熟练度之前，占卜不收钱'
$early = Post-J '/api/divine' @{ byId = 'sage'; kind = 'crystal' }
Check '现在的熟练度还不够开张' ($early.proficiency -lt $early.paidFrom) ("熟练度 {0}，开张线 {1}" -f $early.proficiency, $early.paidFrom)

Write-Host '=== 8. 把熟练度推到开张线'
$guard = 0
while ($true) {
    $now = (Get-J '/api/holdings/sage').proficiency
    if ($now -ge 30 -or $guard -gt 40) { break }
    Post-J '/api/divine' @{ byId = 'sage'; kind = 'tarot' } | Out-Null
    $guard++
}
$ready = Get-J '/api/holdings/sage'
Check '熟练度到了开张线' ($ready.proficiency -ge $ready.paidFrom) ("熟练度 {0} / {1}，用了 {2} 次占卜" -f $ready.proficiency, $ready.paidFrom, $guard)

Write-Host '=== 9. 开张之后，问卜要付钱'
$bobBefore = (Get-J '/api/character/bob').attributes.reserve
$paid = Post-J '/api/divine' @{ byId = 'sage'; kind = 'tarot'; targetId = 'bob'; question = '我该往东边走吗'; advice = '别去东边，今晚有雾' }
$bobAfter = (Get-J '/api/character/bob').attributes.reserve
Check '问卜的人付了钱' ($paid.earned -gt 0) ("earned {0}，价 {1}" -f $paid.earned, $paid.price)
Check '付的钱从问卜者身上扣' ($bobAfter -eq ($bobBefore - $paid.earned)) ("余力 {0} → {1}" -f $bobBefore, $bobAfter)
Check '术士留下了建议' ($paid.advice -eq '别去东边，今晚有雾') ("advice：{0}" -f $paid.advice)

Write-Host '=== 10. 建议进了被建议者的账上'
$bob = Get-J '/api/character/bob'
Check '被建议者身上挂着这条建议' ($bob.advice.Count -gt 0) $(if ($bob.advice.Count -gt 0) { $bob.advice[0].text } else { '读不到 advice 字段' })

Write-Host '=== 11. 存档记得住三层'
Post-J '/api/save' $null | Out-Null
$saved = Get-J '/api/holdings/sage'
Check '存档之后随身两件都在' ($saved.carried.Count -eq 2) ("{0} 件" -f $saved.carried.Count)
Check '存档之后扩建还站在那块区域里' (($saved.buildings | Where-Object { $_.kind -eq 'observatory' }).onPlot -eq $backyard) ("onPlot {0}" -f ($saved.buildings | Where-Object { $_.kind -eq 'observatory' }).onPlot)

Write-Host '=== 12. 谁解锁了，一眼看得见'
$board = Get-J '/api/astrologers'
Check '榜单上有这个人' (($board.astrologers | Where-Object { $_.id -eq 'sage' }) -ne $null) (($board.astrologers | ForEach-Object { "{0}({1})" -f $_.id, $_.proficiency }) -join '、')

Write-Host ''
if ($fails -eq 0) { Write-Host '全部检查通过' -ForegroundColor Green }
else { Write-Host ("{0} 项检查未通过" -f $fails) -ForegroundColor Red; exit 1 }
