# test-venues.ps1 - 验开店经营：店自带场地、菜单要原料、库存不够不卖、
# 钱不够不卖、卖出去钱和货都动了、店员只能是 supporting。

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

Write-Host '=== 0. 建三个人：店主、客人、店员'
Post-J '/api/loop/stop' $null | Out-Null
Start-Sleep -Milliseconds 300

Post-J '/api/character' @{
    id = 'keeper'; name = 'keeper'; isPlayer = $false
    sheet  = @{ role = '店主'; traits = @(); goals = @(); fears = @(); secrets = @(); relations = @{} }
    visual = @{ kind = 'none'; expressions = @() }
    agent  = @{ kind = 'llm'; autonomy = 'guided' }
} | Out-Null

Post-J '/api/character' @{
    id = 'guest'; name = 'guest'; isPlayer = $false
    sheet  = @{ role = '客人'; traits = @(); goals = @(); fears = @(); secrets = @(); relations = @{} }
    visual = @{ kind = 'none'; expressions = @() }
    agent  = @{ kind = 'llm'; autonomy = 'guided' }
} | Out-Null

# 店员是 supporting：不占台上那六十四个席位，所以能一边当店员一边不算在活动人数里。
Post-J '/api/character' @{
    id = 'clerk'; name = 'clerk'; isPlayer = $false; tier = 'supporting'
    sheet  = @{ role = '店员'; traits = @(); goals = @(); fears = @(); secrets = @(); relations = @{} }
    visual = @{ kind = 'none'; expressions = @() }
    agent  = @{ kind = 'llm'; autonomy = 'guided' }
} | Out-Null

$clerkTier = (Get-J '/api/character/clerk').tier
Check '店员确实是 supporting' ($clerkTier -eq 'supporting') ("tier {0}" -f $clerkTier)

Write-Host '=== 1. 开店'
$shopName = '灯下书房-' + (Get-Date -Format 'HHmmss')
$venue = Post-J '/api/venue' @{ ownerId = 'keeper'; name = $shopName; kind = 'cafe' }
Check '店开起来了' ($venue.id.Length -gt 0) ("{0} / {1}" -f $venue.name, $venue.kindLabel)
Check '店自带柜台与座位' ($venue.plots.Count -eq 2) (($venue.plots | ForEach-Object { $_.label }) -join '、')
Check '默认是关着门的' (-not $venue.open) ("open {0}" -f $venue.open)

$code = Status-Of { Post-J '/api/venue' @{ ownerId = 'keeper'; name = $shopName } }
Check '同名不能开第二家' ($code -eq 409) ("HTTP {0}" -f $code)

$vid = $venue.id

Write-Host '=== 2. 关着门卖不出东西'
Post-J '/api/venue/menu' @{ venueId = $vid; name = '温牛奶'; price = 6; effort = 1
    needs = @(@{ item = '牛奶'; count = 1 }) } | Out-Null
$code = Status-Of { Post-J '/api/venue/order' @{ venueId = $vid; guestId = 'guest'; dish = '温牛奶' } }
Check '打烊时点单被拒绝' ($code -eq 409) ("HTTP {0}" -f $code)

Write-Host '=== 3. 菜单'
$code = Status-Of { Post-J '/api/venue/menu' @{ venueId = $vid; name = '温牛奶'; price = 6 } }
Check '同一道菜不能上两次' ($code -eq 409) ("HTTP {0}" -f $code)

$code = Status-Of { Post-J '/api/venue/menu' @{ venueId = $vid; name = '倒贴的钱'; price = -5 } }
Check '负价被拒绝' ($code -eq 409) ("HTTP {0}" -f $code)

Post-J '/api/venue/menu' @{ venueId = $vid; name = '整只烤鹅'; price = 40; effort = 3
    needs = @(@{ item = '鹅'; count = 1 }) } | Out-Null
Post-J '/api/venue/menu' @{ venueId = $vid; name = '一百年的酒'; price = 999 } | Out-Null
$menu = (Get-J "/api/venue/$vid").menu
Check '菜单上有三道菜' ($menu.Count -eq 3) (($menu | ForEach-Object { "{0}({1})" -f $_.Name, $_.Price }) -join '、')

Write-Host '=== 4. 进货要付钱'
$keeperBefore = (Get-J '/api/character/keeper').attributes.reserve
$stocked = Post-J '/api/venue/stock' @{ venueId = $vid; item = '牛奶'; count = 5; unitCost = 2; payerId = 'keeper' }
$keeperAfter = (Get-J '/api/character/keeper').attributes.reserve
Check '库里进了五份牛奶' ($stocked.count -eq 5) ("{0} 份" -f $stocked.count)
Check '进价从店主余力里扣' ($keeperAfter -eq ($keeperBefore - 10)) ("余力 {0} → {1}" -f $keeperBefore, $keeperAfter)
Check '账本记了这笔支出' ($stocked.balance -eq -10) ("balance {0}" -f $stocked.balance)

Write-Host '=== 5. 原料不够就做不出来'
Post-J '/api/venue/toggle' @{ venueId = $vid; open = $true } | Out-Null
$detail = Get-J "/api/venue/$vid"
Check '店已经开张' ($detail.open) ("open {0}" -f $detail.open)

$code = Status-Of { Post-J '/api/venue/order' @{ venueId = $vid; guestId = 'guest'; dish = '整只烤鹅' } }
Check '没有鹅就做不出烤鹅' ($code -eq 409) ("HTTP {0}" -f $code)

Write-Host '=== 6. 钱不够就卖不出去'
$code = Status-Of { Post-J '/api/venue/order' @{ venueId = $vid; guestId = 'guest'; dish = '一百年的酒' } }
Check '买不起就不卖' ($code -eq 409) ("HTTP {0}" -f $code)

Write-Host '=== 7. 店员只能是 supporting'
$code = Status-Of { Post-J '/api/venue/staff' @{ venueId = $vid; staffId = 'keeper'; wage = 3 } }
Check '主角色不能占店员的位置' ($code -eq 409) ("HTTP {0}" -f $code)

$hired = Post-J '/api/venue/staff' @{ venueId = $vid; staffId = 'clerk'; wage = 3 }
Check '店员雇进来了' ($hired.staff -eq 'clerk') ("{0} / {1} / 工钱 {2}" -f $hired.staff, $hired.role, $hired.wage)

Write-Host '=== 8. 一次点单，钱货两清'
$guestBefore = (Get-J '/api/character/guest').attributes.reserve
$keeperBefore2 = (Get-J '/api/character/keeper').attributes.reserve
$served = Post-J '/api/venue/order' @{ venueId = $vid; guestId = 'guest'; dish = '温牛奶'; serverId = 'clerk' }
$guestAfter = (Get-J '/api/character/guest').attributes.reserve
$keeperAfter2 = (Get-J '/api/character/keeper').attributes.reserve

Check '客人付了钱' ($guestAfter -eq ($guestBefore - $served.price)) ("余力 {0} → {1}，价 {2}" -f $guestBefore, $guestAfter, $served.price)
Check '店主收了钱' ($keeperAfter2 -eq ($keeperBefore2 + $served.price)) ("余力 {0} → {1}" -f $keeperBefore2, $keeperAfter2)

$after = Get-J "/api/venue/$vid"
$milk = ($after.stock | Where-Object { $_.Name -eq '牛奶' }).Count
Check '库存少了一份' ($milk -eq 4) ("牛奶 5 → {0}（卖出 1 份）" -f $milk)
Check '账本上净额是 -4' ($after.balance -eq -4) ("balance {0}（进货 -10，卖出 +6）" -f $after.balance)

Write-Host '=== 9. 客人喝下去，人确实放松了'
Check '这一单有回执' ($served.effects.strain -eq -3) ("strain {0}" -f $served.effects.strain)

Write-Host '=== 10. 账上不够就发不出工钱'
$wages = Post-J '/api/venue/wages' @{ venueId = $vid }
Check '账上不够，记下了缺口' ($wages.shortfall -eq 3) ("paid {0}，shortfall {1}" -f $wages.paid, $wages.shortfall)
Check '一分钱也没发出去' ($wages.paid -eq 0) ("paid {0}" -f $wages.paid)

Write-Host '=== 11. 多卖几杯，账就转过来了'
for ($i = 0; $i -lt 3; $i++) {
    Post-J '/api/venue/order' @{ venueId = $vid; guestId = 'guest'; dish = '温牛奶'; serverId = 'clerk' } | Out-Null
}
$rich = Post-J '/api/venue/wages' @{ venueId = $vid }
Check '账上有钱了，工钱发得出去' ($rich.paid -eq 3) ("paid {0}，shortfall {1}，balance {2}" -f $rich.paid, $rich.shortfall, $rich.balance)

Write-Host '=== 12. 存档记得住整间店'
Post-J '/api/save' $null | Out-Null
$saved = Get-J "/api/venue/$vid"
Check '存档之后菜单还在' ($saved.menu.Count -eq 3) ("{0} 道菜" -f $saved.menu.Count)
Check '存档之后库存还在' (($saved.stock | Where-Object { $_.Name -eq '牛奶' }).Count -eq 1) ("牛奶 {0} 份" -f ($saved.stock | Where-Object { $_.Name -eq '牛奶' }).Count)
Check '存档之后店员还在' ($saved.staff.Count -eq 1) ("{0} 人当值" -f $saved.staff.Count)

Write-Host '=== 13. 世界一览能看见这家店'
$list = Get-J '/api/venues'
$mine = $list.venues | Where-Object { $_.id -eq $vid }
Check '店在列表里' ($mine -ne $null) ("{0}，余额 {1}，{2} 人在当值" -f $mine.name, $mine.balance, $mine.staff)

Write-Host ''
if ($fails -eq 0) { Write-Host '全部检查通过' -ForegroundColor Green }
else { Write-Host ("{0} 项检查未通过" -f $fails) -ForegroundColor Red; exit 1 }
