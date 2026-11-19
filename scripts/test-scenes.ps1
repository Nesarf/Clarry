# test-scenes.ps1 - 验多幕：配对真的开出若干幕、一个人不能同时在两幕、收场后让位。

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

Write-Host '=== 0. 清场，建五个人'
Post-J '/api/loop/stop' $null | Out-Null
Post-J '/api/scene/new?clueGoal=99' $null | Out-Null
$names = @('alice', 'bob', 'carol', 'dave', 'erin')
foreach ($id in $names) {
    Post-J '/api/character' @{
        id = $id; name = $id; isPlayer = $false
        sheet  = @{ role = '居民'; traits = @(); goals = @(); fears = @(); secrets = @(); relations = @{} }
        visual = @{ kind = 'none'; expressions = @() }
        agent  = @{ kind = 'llm'; autonomy = 'guided' }
    } | Out-Null
    Post-J '/api/stage/enter' @{ id = $id } | Out-Null
}

Write-Host '=== 1. 按配对规则一次开好所有幕'
$auto = Post-J '/api/scenes/auto' $null
$auto.opened | ForEach-Object { '    {0}  第{1}幕  {2}  —— {3}' -f $_.id, $_.number, ($_.cast -join '、'), $_.reason }
Check '开出了多于一幕' ($auto.opened.Count -gt 1) ("{0} 幕" -f $auto.opened.Count)
$allCast = @()
foreach ($s in $auto.opened) { $allCast += $s.cast }

# 库里还留着以前测试建出来的角色，auto 会把所有居民都配上——这符合「所有人都过日子」，
# 所以这里只断言本次建的五个人都在，不断言总数。
$missing = $names | Where-Object { $allCast -notcontains $_ }
Check '建的五个人都被排进去了' ($missing.Count -eq 0) $(if ($missing) { '缺：' + ($missing -join '、') } else { '全在' })
Check '没有人被分到两幕里' (($allCast | Sort-Object -Unique).Count -eq $allCast.Count) ($allCast -join '、')

Write-Host '=== 2. 世界视角能看见这几幕'
$scenes = Get-J '/api/scenes'
Check '开放的幕数与刚才一致' ($scenes.open.Count -eq $auto.opened.Count) ("{0} 幕开着的" -f $scenes.open.Count)
Check '容量是全世界一起算的' ($scenes.capacity -eq 64) ("capacity {0}, occupied {1}, room {2}" -f $scenes.capacity, $scenes.occupied, $scenes.room)

Write-Host '=== 3. 一个人不能同时在两幕'
$code = 0
try {
    Post-J '/api/scenes/open' @{ cast = @('alice', 'bob'); title = '重开一幕' } | Out-Null
    $code = 200
} catch { $code = [int]$_.Exception.Response.StatusCode }
Check '已在这一幕里的人不能再开一幕' ($code -eq 409) ("HTTP {0}" -f $code)

Write-Host '=== 4. 收掉一幕，人就空出来了'
$target = $scenes.open[0]
$closed = Post-J "/api/scenes/close?sceneId=$($target.id)" $null
Check '那一幕已收' ($closed.wasOpen) ("{0}" -f $target.id)
Check '占用减少' ($closed.occupied -lt $scenes.occupied) ("{0} → {1}" -f $scenes.occupied, $closed.occupied)

$freed = @($target.cast)[0]
$code = 0
try {
    Post-J '/api/scenes/open' @{ cast = @($freed); title = '一个人的一幕' } | Out-Null
    $code = 200
} catch { $code = [int]$_.Exception.Response.StatusCode }
Check '收场之后那个人可以重新入幕' ($code -eq 200) ("HTTP {0}（{1}）" -f $code, $freed)

Write-Host '=== 5. 存档记得住多幕'
Post-J '/api/save' $null | Out-Null
$after = Get-J '/api/scenes'
Check '开放的幕都还在' ($after.open.Count -ge 1) ("{0} 幕开着，{1} 幕已收" -f $after.open.Count, $after.closed.Count)
Check '收掉的那幕留在账上' ($after.closed.Count -ge 1) ("已收 {0} 幕" -f $after.closed.Count)

Write-Host ''
if ($fails -eq 0) { Write-Host '全部检查通过' -ForegroundColor Green }
else { Write-Host ("{0} 项检查未通过" -f $fails) -ForegroundColor Red; exit 1 }
