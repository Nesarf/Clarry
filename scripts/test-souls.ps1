# test-souls.ps1 - 验两条从魂系借来的规则：余火被点燃与烧尽，压力撞顶之后的失名。
#
# 这两条都不是数值装饰。余火要能证明「说出来的人被点着，而且是下一轮才拿到火」；
# 失名要能证明「压力到顶不是一个封顶，是一个代价」，代价落在具体的人身上。

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

Write-Host '=== 0. 清场并把三人留在台上'
Post-J '/api/loop/stop' $null | Out-Null
foreach ($id in @('alice', 'bob', 'carol')) { Post-J '/api/stage/enter' @{ id = $id } | Out-Null }
Post-J '/api/scene/new?clueGoal=99' $null | Out-Null
Check '三人都在台上' (((Get-J '/api/cast').Count) -ge 3) ((Get-J '/api/world').stage -join '、')

Write-Host '=== 1. 先给爱丽丝一段羁绊（余火与失名都要用得上）'
Post-J '/api/round/begin' $null | Out-Null
Post-J '/api/round/order' @{ actorId = 'alice'; kind = 'give'; targetId = 'bob'; text = '拿着，别弄丢。' } | Out-Null
Post-J '/api/round/resolve' $null | Out-Null
$alice = Who 'alice'
Check '羁绊已建立' ($alice.a.bond.bob -gt 0) ("alice → bob 温度 {0}" -f $alice.a.bond.bob)

Write-Host '=== 2. 说出真相：应当点亮余火，但不在这一轮生效'
$before = Who 'alice'
Post-J '/api/round/begin' $null | Out-Null
Post-J '/api/round/order' @{ actorId = 'alice'; kind = 'reveal'; text = '那把剑不是我家的，是别人塞给我的。' } | Out-Null
Post-J '/api/round/resolve' $null | Out-Null
$afterReveal = Who 'alice'
Check '余火被点亮' ([bool]$afterReveal.a.ember) ("ember={0}" -f $afterReveal.a.ember)
Check '声望按揭露结算（+6）' (($afterReveal.a.renown - $before.a.renown) -eq 6) ("{0} → {1}" -f $before.a.renown, $afterReveal.a.renown)

Write-Host '=== 3. 带火出手：声望翻倍，压力多付，火熄灭'
$before = Who 'alice'
Post-J '/api/round/begin' $null | Out-Null
Post-J '/api/round/order' @{ actorId = 'alice'; kind = 'act'; text = '她一步跨到门口，把退路堵死。' } | Out-Null
$res = Post-J '/api/round/resolve' $null
$afterAct = Who 'alice'
Check '余火已烧尽' (-not [bool]$afterAct.a.ember) ''
Check '声望按余火结算（+4，不是 +2）' (($afterAct.a.renown - $before.a.renown) -eq 4) ("{0} → {1}" -f $before.a.renown, $afterAct.a.renown)
Check '压力按余火结算（+7，不是 +1）' (($afterAct.a.strain - $before.a.strain) -eq 7) ("{0} → {1}" -f $before.a.strain, $afterAct.a.strain)
$note = $res.notes | Where-Object { $_ -like '*余火*' } | Select-Object -First 1
Check '结算备注里写清了这笔账' (-not [string]::IsNullOrWhiteSpace($note)) $note

Write-Host '=== 4. 把压力推到 100：应当失名，忘掉羁绊最薄的那个人'
$before = Who 'alice'
$need = 100 - $before.a.strain
if ($need -lt 1) { $need = 1 }
Write-Host ("  爱丽丝当前压力 {0}，需要再提 {1} 次沉默" -f $before.a.strain, $need)
Post-J '/api/round/begin' $null | Out-Null
for ($i = 0; $i -lt $need; $i++) {
    Post-J '/api/round/order' @{ actorId = 'alice'; kind = 'withhold'; text = '……' } | Out-Null
}
$res2 = Post-J '/api/round/resolve' $null
$after = Who 'alice'
Check '压力回落到 55（不是封在 100）' ($after.a.strain -eq 55) ("strain={0}" -f $after.a.strain)
Check '声望因失名下降 4' (($after.a.renown - $before.a.renown) -eq -4) ("{0} → {1}" -f $before.a.renown, $after.a.renown)
Check '羁绊最薄的那个人被忘了' ($null -eq $after.a.bond.bob) ("剩余羁绊：{0}" -f (($after.a.bond.PSObject.Properties.Name) -join '、'))
Check '被遗忘的人也收到了记录' ([bool](($res2.revealed | Where-Object { $_ -like '*忘了*' }).Count)) (($res2.revealed -join '；'))
$note2 = $res2.notes | Where-Object { $_ -like '*失名*' } | Select-Object -First 1
Check '结算备注里写清了这笔账' (-not [string]::IsNullOrWhiteSpace($note2)) $note2

Write-Host '=== 5. 余力：这轮没花就该回一点（借 AI LIMIT 的同步率结构）'
$before = Who 'alice'
Post-J '/api/round/begin' $null | Out-Null
Post-J '/api/round/order' @{ actorId = 'alice'; kind = 'speak'; text = '……' } | Out-Null
Post-J '/api/round/resolve' $null | Out-Null
$after = Who 'alice'
Check '没花余力的角色回了 3 点' (($after.a.reserve - $before.a.reserve) -eq 3) ("{0} → {1}" -f $before.a.reserve, $after.a.reserve)

$before = Who 'alice'
Post-J '/api/round/begin' $null | Out-Null
Post-J '/api/round/order' @{ actorId = 'alice'; kind = 'probe'; targetId = 'carol'; text = '看她拨弦的手。' } | Out-Null
Post-J '/api/round/resolve' $null | Out-Null
$after = Who 'alice'
Check '花了余力的角色不回复' (($after.a.reserve - $before.a.reserve) -eq -1) ("{0} → {1}" -f $before.a.reserve, $after.a.reserve)

Write-Host ''
if ($fails -eq 0) { Write-Host '全部检查通过' -ForegroundColor Green }
else { Write-Host ("{0} 项检查未通过" -f $fails) -ForegroundColor Red; exit 1 }
