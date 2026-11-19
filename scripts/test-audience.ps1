# test-audience.ps1 - 验观众视线：默认跟最近动过的那一幕、可以钉住、钉住之后
# 别处再热闹也不动、许愿能把镜头挪走、钉住的那幕收了就自动回落。

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

Write-Host '=== 0. 清场，重置许愿池'
Post-J '/api/loop/stop' $null | Out-Null
Start-Sleep -Milliseconds 300
foreach ($s in (Get-J '/api/scenes').open) {
    Post-J "/api/scenes/close?sceneId=$($s.id)" $null | Out-Null
}
# scene/new 只重置活跃幕，多幕之后没有开着的幕时它会自建一个空幕再把许愿池填满。
Post-J '/api/scene/new?clueGoal=99' $null | Out-Null
foreach ($s in (Get-J '/api/scenes').open) {
    Post-J "/api/scenes/close?sceneId=$($s.id)" $null | Out-Null
}

$names = @('alice', 'bob', 'carol', 'dave')
foreach ($id in $names) {
    Post-J '/api/character' @{
        id = $id; name = $id; isPlayer = $false
        sheet  = @{ role = '居民'; traits = @(); goals = @(); fears = @(); secrets = @(); relations = @{} }
        visual = @{ kind = 'none'; expressions = @() }
        agent  = @{ kind = 'llm'; autonomy = 'guided' }
    } | Out-Null
}

Write-Host '=== 1. 开两幕'
$a = Post-J '/api/scenes/open' @{ cast = @('alice', 'bob');   title = '甲幕' }
$b = Post-J '/api/scenes/open' @{ cast = @('carol', 'dave'); title = '乙幕' }
Check '两幕都开了' ($a.opened -ne $b.opened) ("{0}、{1}" -f $a.opened, $b.opened)

Write-Host '=== 2. 默认跟最近动过的那一幕'
$seed = Post-J '/api/scene/narrate' @{ sceneId = $b.opened; text = '乙幕里有人碰倒了杯子' }
$aud = Get-J '/api/audience'
Check '镜头跟着刚动过的乙幕' ($aud.watching -eq $b.opened) ("在看 {0}" -f $aud.watching)
Check '说的是「最近动过」' ($aud.because -match '最近动过') $aud.because
Check '没有钉在任何一幕' ($null -eq $aud.pinned) ("pinned {0}" -f $aud.pinned)

$moved = Post-J '/api/scene/narrate' @{ sceneId = $a.opened; text = '甲幕的灯亮了一下' }
$aud2 = Get-J '/api/audience'
Check '甲幕一动，镜头就跟过去' ($aud2.watching -eq $a.opened) ("在看 {0}" -f $aud2.watching)

Write-Host '=== 3. 钉住一幕'
$pinned = Post-J '/api/audience/follow' @{ sceneId = $b.opened }
Check '钉到了乙幕' ($pinned.watching -eq $b.opened) ("在看 {0}" -f $pinned.watching)
Check '理由是钉住的' ($pinned.because -match '钉住') $pinned.because

$noise = Post-J '/api/scene/narrate' @{ sceneId = $a.opened; text = '甲幕里有人喊了一声' }
$aud3 = Get-J '/api/audience'
Check '别处再热闹，镜头不动' ($aud3.watching -eq $b.opened) ("甲幕动了，镜头仍在 {0}" -f $aud3.watching)

Write-Host '=== 4. 回到自动'
$auto = Post-J '/api/audience/auto' $null
Check '自动之后跟到刚动过的甲幕' ($auto.watching -eq $a.opened) ("在看 {0}" -f $auto.watching)
Check '理由是最近动过' ($auto.because -match '最近动过') $auto.because

Write-Host '=== 5. 许愿能把镜头挪走'
$before = (Get-J '/api/audience').wishes.left
Post-J '/api/audience/follow' @{ sceneId = $b.opened } | Out-Null
$wished = Post-J '/api/wish' @{ kind = 'focus'; subjectId = $a.opened }
$after = Get-J '/api/audience'
Check '镜头挪到了甲幕' ($after.watching -eq $a.opened) ("甲幕动了，但许愿把镜头留在了 {0}" -f $after.watching)
Check '许愿池少了一次' ($after.wishes.left -eq ($before - 1)) ("{0} → {1}" -f $before, $after.wishes.left)

$code = 0
try { Post-J '/api/wish' @{ kind = 'focus'; subjectId = 'scene-nosuch' } | Out-Null; $code = 200 }
catch { $code = [int]$_.Exception.Response.StatusCode }
Check '不许把镜头挪到不存在的幕' ($code -eq 404) ("HTTP {0}" -f $code)

Write-Host '=== 6. 钉住的那幕收了，镜头自己回落'
$stillA = (Get-J '/api/audience').watching
$closing = Post-J "/api/scenes/close?sceneId=$($stillA)" $null
$fell = Get-J '/api/audience'
Check '收掉正在看的那一幕' ($closing.wasOpen) ("{0}" -f $stillA)
Check '镜头落到了还开着的那一幕' ($fell.watching -eq $b.opened) ("在看 {0}" -f $fell.watching)

Write-Host '=== 7. 历史按幕取'
$hist = Get-J "/api/history?take=20&scene=$($b.opened)"
$all = ($hist | ForEach-Object { $_.text }) -join ' / '
Check '乙幕的历史里只有乙幕的事' (($all -match '杯子') -and ($all -notmatch '喊了一声')) $all
Check '没有这一幕时给 404' ((Status-Of { Get-J '/api/history?scene=scene-nosuch' }) -eq 404) 'HTTP 404'

Write-Host '=== 8. 存档记得住钉在哪一幕'
Post-J '/api/audience/follow' @{ sceneId = $b.opened } | Out-Null
Post-J '/api/save' $null | Out-Null
$saved = Get-J '/api/audience'
Check '存档之后仍钉在乙幕' ($saved.pinned -eq $b.opened) ("pinned {0}" -f $saved.pinned)

Write-Host ''
if ($fails -eq 0) { Write-Host '全部检查通过' -ForegroundColor Green }
else { Write-Host ("{0} 项检查未通过" -f $fails) -ForegroundColor Red; exit 1 }
