# test-address-and-vigil.ps1 - 验两样：
#   一、称呼：起一个叫法会同时升温，并且真的存了下来（之后会被喂进 prompt）
#   二、守夜：合作局没有赢家，只有「一起撑住」或「一起没撑住」

$base = 'http://127.0.0.1:3090'

function Get-J([string]$p) { Invoke-RestMethod -Uri "$base$p" -Method Get -TimeoutSec 30 }
function Post-J([string]$p, $b) {
    if ($null -eq $b) { Invoke-RestMethod -Uri "$base$p" -Method Post -TimeoutSec 60 }
    else {
        Invoke-RestMethod -Uri "$base$p" -Method Post -TimeoutSec 60 `
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

Write-Host '=== 一、称呼'
$before = Who 'alice'
$r = Post-J '/api/address' @{ actorId = 'alice'; targetId = 'carol'; nickname = '琴匣子' }
Check '称呼被记下' ($r.nickname -eq '琴匣子') $r.nickname
$after = Who 'alice'
Check '称呼确实存进了角色' ($after.a.names.carol -eq '琴匣子') ("alice 管 carol 叫 {0}" -f $after.a.names.carol)
Check '被称呼的一方也被温热' ((Who 'carol').a.bond.alice -gt (Who 'alice' | ForEach-Object { 0 })) ("carol → alice 温度 {0}" -f (Who 'carol').a.bond.alice)

$long = 0
try { Post-J '/api/address' @{ actorId = 'alice'; targetId = 'bob'; nickname = ('长' * 20) } | Out-Null; $long = 200 }
catch { $long = [int]$_.Exception.Response.StatusCode }
Check '过长的称呼被拒绝' ($long -eq 400) ("HTTP {0}" -f $long)

Write-Host '=== 二、守夜（合作局）'
$v = Post-J '/api/minigame/vigil' $null
Check '有团队结果而没有赢家' ($null -ne $v.teamSucceeded) ("teamSucceeded={0}" -f $v.teamSucceeded)
Check '参与人数在 2 到 4 之间' ($v.players.Count -ge 2 -and $v.players.Count -le 4) ("{0} 人：{1}" -f $v.players.Count, ($v.players -join '、'))
Check '没有用随机' (-not [bool]$v.usedDice) ''
$v.log | ForEach-Object { '    ' + $_ }

Write-Host '=== 三、出力确实从属性算出来，不是写死的'
$w = Who 'alice'
$expected = [math]::Floor($w.a.poise / 20) + [math]::Floor($w.a.reserve / 40)
$m = [regex]::Match(($v.log -join "`n"), 'alice 出力 (\d+)')
Check '出力等于 poise/20 + reserve/40' ($m.Success -and [int]$m.Groups[1].Value -eq $expected) `
    ("算出来 {0}，日志里 {1}" -f $expected, $(if ($m.Success) { $m.Groups[1].Value } else { '无' }))

Write-Host '=== 四、把余力磨掉，出力应当跟着掉'
for ($round = 0; $round -lt 12; $round++) {
    Post-J '/api/round/begin' $null | Out-Null
    Post-J '/api/round/order' @{ actorId = 'alice'; kind = 'give'; targetId = 'bob'; text = '给你。' } | Out-Null
    Post-J '/api/round/order' @{ actorId = 'alice'; kind = 'give'; targetId = 'bob'; text = '还有这个。' } | Out-Null
    Post-J '/api/round/resolve' $null | Out-Null
}
$w2 = Who 'alice'
$expected2 = [math]::Floor($w2.a.poise / 20) + [math]::Floor($w2.a.reserve / 40)
$v2 = Post-J '/api/minigame/vigil' $null
$m2 = [regex]::Match(($v2.log -join "`n"), 'alice 出力 (\d+)')
Check '余力掉下去之后出力重新算对了' ($m2.Success -and [int]$m2.Groups[1].Value -eq $expected2) `
    ("余力 {0}，算出 {1}，日志 {2}" -f $w2.a.reserve, $expected2, $(if ($m2.Success) { $m2.Groups[1].Value } else { '无' }))
Check '出力确实比之前低' ($m2.Success -and [int]$m2.Groups[1].Value -lt [int]$m.Groups[1].Value) `
    ("{0} → {1}" -f $m.Groups[1].Value, $(if ($m2.Success) { $m2.Groups[1].Value } else { '无' }))

Write-Host ''
if ($fails -eq 0) { Write-Host '全部检查通过' -ForegroundColor Green }
else { Write-Host ("{0} 项检查未通过" -f $fails) -ForegroundColor Red; exit 1 }
