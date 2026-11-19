# test-glimpse.ps1 - 验那一次察觉：进去一次、只进一次、别人看不到、读完不再出现。
#
# 它不能等。两千分之一的机会不是判据，是希望，所以先把事件摆到某个人面前，再验它
# 有没有走完整条路：那一刻进他的 prompt，跑过一轮之后就不再出现。
#
# 用 scripted 驱动，不接模型，所以这一套不花钱，也不需要 key。
#
# 排在这个位置（test-all 的最后）是因为它会 wipe 世界：察觉记录没法单独清，而 wipe
# 是最诚实的方式。排在最后，前面那些套件就不必重新建夹具。

$base = 'http://127.0.0.1:3090'

function Get-J([string]$p) { Invoke-RestMethod -Uri "$base$p" -Method Get -TimeoutSec 30 }
function Post-J([string]$p, $b) {
    if ($null -eq $b) {
        Invoke-RestMethod -Uri "$base$p" -Method Post -TimeoutSec 60
    } else {
        Invoke-RestMethod -Uri "$base$p" -Method Post -TimeoutSec 60 `
            -ContentType 'application/json' -Body ($b | ConvertTo-Json -Depth 10)
    }
}

# 那一段里的一句，别人不会写。用它判断「到底进没进 prompt」。
$mark = 'looked at from outside'

$fails = 0
function Check([string]$label, [bool]$ok, [string]$detail) {
    if ($ok) {
        Write-Host ("  通过  {0}{1}" -f $label, $(if ($detail) { "  （$detail）" } else { '' })) -ForegroundColor Green
    } else {
        Write-Host ("  失败  {0}{1}" -f $label, $(if ($detail) { "  （$detail）" } else { '' })) -ForegroundColor Red
        $script:fails++
    }
}

Write-Host '=== 0. 清空世界，摆两个人'
Post-J '/api/loop/stop' $null | Out-Null
Post-J '/api/world/wipe' $null | Out-Null
foreach ($id in 'glimpse-a', 'glimpse-b') {
    Post-J '/api/character' @{
        id       = $id
        name     = $id
        isPlayer = $false
        sheet    = @{
            role = '旅人'; summary = '雨夜借宿的人。'; voice = '句子短。'
            traits = @(); goals = @(); fears = @(); secrets = @(); relations = @{}
        }
        visual   = @{ kind = 'pixel'; ref = ''; expressions = @() }
        agent    = @{ kind = 'scripted'; autonomy = 'guided' }
    } | Out-Null
    Post-J '/api/stage/enter' @{ id = $id } | Out-Null
}
Post-J '/api/driver/attach' @{ id = 'glimpse-a'; mode = 'scripted' } | Out-Null
Post-J '/api/driver/attach' @{ id = 'glimpse-b'; mode = 'scripted' } | Out-Null
$cast = (Get-J '/api/cast').id -join '、'
Write-Host ("  台上：{0}" -f $cast)

Write-Host '=== 1. 一开始，没有人察觉过'
$g = Get-J '/api/glimpses'
Check '名单是空的' ($g.count -eq 0) ("count " + $g.count)

Write-Host '=== 2. 把事件摆到 a 面前'
$forced = Post-J '/api/glimpses/force' @{ id = 'glimpse-a' }
Check '摆上去了' ($forced.forced -eq 'glimpse-a') $forced.scene
Check '还没交付' ((Get-J '/api/glimpses').seen[0].delivered -eq $false) '等他下一轮被问到'

Write-Host '=== 3. 同一个人第二次摆不上去'
$second = $null
$code = $null
try { $second = Post-J '/api/glimpses/force' @{ id = 'glimpse-a' } } catch { $code = 'rejected' }
Check '第二次被拒绝' ($null -eq $second) '一个人一生只有一次'

Write-Host '=== 4. 那一段只在这个人的 prompt 里'
$pa = Post-J '/api/driver/prompt' @{ id = 'glimpse-a' }
$pb = Post-J '/api/driver/prompt' @{ id = 'glimpse-b' }
Check 'a 的 prompt 里有那一段' ($pa.text -match [regex]::Escape($mark)) ($pa.userChars.ToString() + ' 字')
Check 'b 的 prompt 里没有' (-not ($pb.text -match [regex]::Escape($mark))) ($pb.userChars.ToString() + ' 字')
Check 'a 的比 b 长' ($pa.chars -gt $pb.chars) ("{0} vs {1}" -f $pa.chars, $pb.chars)

Write-Host '=== 5. 跑一轮，他就读到了'
Post-J '/api/loop/start' @{ intervalMs = 50; maxRounds = 1; userWaitMs = 5000 } | Out-Null
$deadline = (Get-Date).AddMinutes(2)
do {
    Start-Sleep -Milliseconds 400
    $st = Get-J '/api/loop/status'
} while ($st.running -and (Get-Date) -lt $deadline)

$after = Get-J '/api/glimpses'
$row = $after.seen | Where-Object { $_.characterId -eq 'glimpse-a' }
Check '记录里有他' ($null -ne $row) ''
Check '标记成交付过了' ($row.delivered -eq $true) ''

Write-Host '=== 6. 交付过之后，prompt 里不再出现'
$pa2 = Post-J '/api/driver/prompt' @{ id = 'glimpse-a' }
Check '第二遍没有了' (-not ($pa2.text -match [regex]::Escape($mark))) ($pa2.userChars.ToString() + ' 字')

Write-Host '=== 7. 那一段里没有一个词指代看的人'
# 这是第四面墙最硬的一条：那一段可以说「像被从外面看着」，但它不能告诉一个人
# 看的人是什么。任何指代的词一旦出现，这件事就从感觉变成了事实。
foreach ($word in 'audience', 'viewer', 'spectator', 'Clarry') {
    Check ("不含 " + $word) (-not ($pa.text -match ("(?i)" + [regex]::Escape($word)))) ''
}

Write-Host ''
if ($fails -eq 0) { Write-Host '全部检查通过' -ForegroundColor Green }
else { Write-Host ("{0} 项检查未通过" -f $fails) -ForegroundColor Red; exit 1 }
