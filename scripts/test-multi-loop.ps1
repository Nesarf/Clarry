# test-multi-loop.ps1 - 验多幕同时推进：一拍里每一幕各走一轮、两幕的话不串场、
# 收掉一幕不影响别的一幕。这是 multi-loop 的判据。

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

$fails = 0
function Check([string]$label, [bool]$ok, [string]$detail) {
    if ($ok) {
        Write-Host ("  通过  {0}{1}" -f $label, $(if ($detail) { "  （$detail）" } else { '' })) -ForegroundColor Green
    } else {
        Write-Host ("  失败  {0}{1}" -f $label, $(if ($detail) { "  （$detail）" } else { '' })) -ForegroundColor Red
        $script:fails++
    }
}

Write-Host '=== 0. 收场、收掉所有开着的幕'
Post-J '/api/loop/stop' $null | Out-Null
Start-Sleep -Milliseconds 300
$before = Get-J '/api/scenes'
foreach ($s in $before.open) {
    Post-J "/api/scenes/close?sceneId=$($s.id)" $null | Out-Null
}
$clean = Get-J '/api/scenes'
Check '所有幕都收了' ($clean.open.Count -eq 0) ("开着 {0} 幕" -f $clean.open.Count)

Write-Host '=== 1. 建四个人，开两幕，各两人'
$names = @('alice', 'bob', 'carol', 'dave')
foreach ($id in $names) {
    Post-J '/api/character' @{
        id = $id; name = $id; isPlayer = $false
        sheet  = @{ role = '居民'; traits = @(); goals = @(); fears = @(); secrets = @(); relations = @{} }
        visual = @{ kind = 'none'; expressions = @() }
        agent  = @{ kind = 'llm'; autonomy = 'guided' }
    } | Out-Null
}

$one = Post-J '/api/scenes/open' @{ cast = @('alice', 'bob');   title = '甲幕' }
$two = Post-J '/api/scenes/open' @{ cast = @('carol', 'dave'); title = '乙幕' }
Check '两幕都开好了' ($one.opened -ne $two.opened) ("{0}、{1}" -f $one.opened, $two.opened)

# 幕的收束条件放宽，免得跑到一半自己散场，断言的轮数就对不上了。
foreach ($id in $names) {
    Post-J '/api/driver/attach' @{ id = $id; mode = 'scripted' } | Out-Null
}
$driven = Get-J '/api/driver/list'
Check '四个人都有驱动器' (($names | Where-Object { $driven.driven -notcontains $_ }).Count -eq 0) (($driven.driven | Where-Object { $names -contains $_ }) -join '、')

Write-Host '=== 2. 跑四拍'
Post-J '/api/loop/start' @{ intervalMs = 120; maxRounds = 4 } | Out-Null

$waited = 0
$status = $null
while ($waited -lt 40000) {
    Start-Sleep -Milliseconds 400
    $waited += 400
    $status = Get-J '/api/loop/status'
    if (-not $status.running) { break }
}
Check '四拍之后自己收了场' ($status -and -not $status.running) $(if ($status) { $status.stopReason } else { '拿不到状态' })
Check '拍数记到了四' ($status.beats -eq 4) ("beats {0}" -f $status.beats)
Check '一拍里所有幕一起动' ($status.openScenes -eq 2) ("openScenes {0}" -f $status.openScenes)

Write-Host '=== 3. 每一幕各走了四轮'
$after = Get-J '/api/scenes'
$sceneOne = $after.open | Where-Object { $_.id -eq $one.opened }
$sceneTwo = $after.open | Where-Object { $_.id -eq $two.opened }
Check '甲幕走了四轮' ($sceneOne -and $sceneOne.rounds -eq 4) $(if ($sceneOne) { "rounds {0}" -f $sceneOne.rounds } else { '甲幕不在了' })
Check '乙幕走了四轮' ($sceneTwo -and $sceneTwo.rounds -eq 4) $(if ($sceneTwo) { "rounds {0}" -f $sceneTwo.rounds } else { '乙幕不在了' })

Write-Host '=== 4. 两幕的话不串场'
$oneLine = $sceneOne.last
$twoLine = $sceneTwo.last
Check '甲幕最后一句是甲幕的人说的' ($oneLine -match 'alice|bob') $oneLine
Check '甲幕里没有乙幕的人' ($oneLine -notmatch 'carol|dave') $oneLine
Check '乙幕最后一句是乙幕的人说的' ($twoLine -match 'carol|dave') $twoLine
Check '乙幕里没有甲幕的人' ($twoLine -notmatch 'alice|bob') $twoLine

Write-Host '=== 5. 收掉一幕，另一幕不受影响'
$closedRes = Post-J "/api/scenes/close?sceneId=$($one.opened)" $null
Check '甲幕收掉了' ($closedRes.wasOpen) ("{0}" -f $one.opened)

$left = Get-J '/api/scenes'
Check '乙幕还开着' (($left.open | Where-Object { $_.id -eq $two.opened }) -ne $null) ("开着：{0}" -f (($left.open | ForEach-Object { $_.id }) -join '、'))

# 收场不是把人删掉：甲幕的人还该能立刻进另一幕。
$again = Post-J '/api/scenes/open' @{ cast = @('alice', 'bob'); title = '甲幕的下一场' }
Check '甲幕的人能重新入幕' ($again.opened -ne $null) ("{0}" -f $again.opened)
Post-J "/api/scenes/close?sceneId=$($again.opened)" $null | Out-Null

Write-Host '=== 6. 再来两拍，只有乙幕会动'
$roundsBeforeTwo = ($left.open | Where-Object { $_.id -eq $two.opened }).rounds
Post-J '/api/loop/start' @{ intervalMs = 120; maxRounds = 2 } | Out-Null
$waited = 0
while ($waited -lt 30000) {
    Start-Sleep -Milliseconds 400
    $waited += 400
    $status = Get-J '/api/loop/status'
    if (-not $status.running) { break }
}
$last = Get-J '/api/scenes'
$roundsAfterTwo = ($last.open | Where-Object { $_.id -eq $two.opened }).rounds
Check '第二次开场，拍数从零算' ($status.beats -eq 2) ("beats {0}" -f $status.beats)
Check '乙幕从四轮走到六轮' ($roundsAfterTwo -eq ($roundsBeforeTwo + 2)) ("{0} → {1}" -f $roundsBeforeTwo, $roundsAfterTwo)
Check '甲幕没有继续走' (($last.closed | Where-Object { $_.id -eq $one.opened }).rounds -eq 4) ("甲幕仍是 {0} 轮" -f ($last.closed | Where-Object { $_.id -eq $one.opened }).rounds)

Write-Host ''
if ($fails -eq 0) { Write-Host '全部检查通过' -ForegroundColor Green }
else { Write-Host ("{0} 项检查未通过" -f $fails) -ForegroundColor Red; exit 1 }
