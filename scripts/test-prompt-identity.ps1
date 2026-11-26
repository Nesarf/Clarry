# test-prompt-identity.ps1 - 验身份认知：模型看到的那段文字里，只有角色，没有幕后。
#
# 判据是扫词。这不是文字洁癖：一个被塞了「agent」这个词的模型会开始谈论它是 agent，
# 而那正是第四面墙破掉的地方。所以这一套测试不检查"提得少"，只检查"一次都不提"。

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

Write-Host '=== 0. 建一个人，让他坐到台上去'
Post-J '/api/loop/stop' $null | Out-Null
Start-Sleep -Milliseconds 300
Post-J '/api/scene/new?clueGoal=99' $null | Out-Null
foreach ($r in (Get-J '/api/tiers').residents) {
    if ($r.seated) { Post-J '/api/stage/leave' @{ id = $r.id } | Out-Null }
}
foreach ($id in @('alice', 'bob')) {
    Post-J '/api/character' @{
        id = $id; name = $id; isPlayer = $false
        sheet  = @{ role = '居民'; traits = @(); goals = @(); fears = @(); secrets = @(); relations = @{} }
        visual = @{ kind = 'none'; expressions = @() }
        agent  = @{ kind = 'llm'; autonomy = 'guided' }
    } | Out-Null
    Post-J '/api/stage/enter' @{ id = $id } | Out-Null
}

$prompt = Post-J '/api/driver/prompt' @{ id = 'alice' }
Check 'prompt 拿得到' ($prompt.text.Length -gt 500) ("{0} 字，约 {1} token" -f $prompt.chars, $prompt.approxTokens)

Write-Host '=== 1. 幕后的词一个都不许有'
$forbidden = @('agent', 'player', 'audience', 'human', 'Clarry', 'theatre', 'model', 'program')
foreach ($word in $forbidden) {
    $hit = [regex]::Matches($prompt.text, [regex]::Escape($word), 'IgnoreCase').Count
    Check ("prompt 里没有「{0}」" -f $word) ($hit -eq 0) ("出现 {0} 次" -f $hit)
}

Write-Host '=== 2. 幕的编号也不进 prompt'
$sceneHit = [regex]::Matches($prompt.text, 'scene-\d+').Count
Check '没有 scene-N 这样的编号' ($sceneHit -eq 0) ("出现 {0} 次" -f $sceneHit)

Write-Host '=== 3. 第一人称就是本人'
Check '开头说的是「你是住在这栋房子里的一个人」' ($prompt.text -match 'You are a person living in a house with other people') '开场第一句'
Check '没有「你在扮演」这种说法' ($prompt.text -notmatch 'playing a character in') 'not playing a character in'

Write-Host '=== 4. 对面是那个人，不是它的背后'
# 同一幕里有 bob，所以 prompt 里应当出现他的 id 与角色，但不该带任何身份标记。
Check '列出了同席的人' ($prompt.text -match 'On stage with you') '有这一段'
Check '带了角色的行当' ($prompt.text -match '居民') '带 role'
$tags = @('(an agent)', '(the human)', 'an agent', 'the human')
foreach ($tag in $tags) {
    Check ("没有「{0}」这种标记" -f $tag) ($prompt.text -notmatch [regex]::Escape($tag)) ''
}

Write-Host '=== 5. 席位是谁在按，与世界外面的事都不提'
Check '没有「草稿会被人看」这类话' ($prompt.text -notmatch 'drafted|will be reviewed') ''
Check '没有 IsPlayer 的痕迹' ($prompt.text -notmatch 'isPlayer|IsPlayer') ''

Write-Host '=== 6. 该在的东西都在'
Check '有规则段' ($prompt.text -match 'Rounds are simultaneous') '有'
Check '有属性段' ($prompt.text -match 'Your state right now') '有'
Check '有天象' ($prompt.text -match 'The sky:') '有'

Write-Host '=== 7. 她怎么看自己，就是第一句'
Post-J '/api/character' @{
    id = 'ruby'; name = '露比'; isPlayer = $false
    sheet  = @{
        role = '猫人'
        selfImage = '一位长得非常漂亮的猫人小姐'
        traits = @(); goals = @(); fears = @(); secrets = @(); relations = @{}
    }
    visual = @{ kind = 'none'; expressions = @() }
    agent  = @{ kind = 'llm'; autonomy = 'guided' }
} | Out-Null
Post-J '/api/stage/enter' @{ id = 'ruby' } | Out-Null

$ruby = Post-J '/api/driver/prompt' @{ id = 'ruby' }
Check '第一句是她的自我认知' ($ruby.text -match 'You are 露比 -- 一位长得非常漂亮的猫人小姐\.') '开头那句'
Check '评价留在里面，不是一串标签' ($ruby.text -match '非常漂亮') '自己说自己漂亮'
Check '紧接着划清「你以为的你」与「别人知道的你」' ($ruby.text -match 'what other people know, which is not always the same thing') '那句分界'

# 别人看她时用的是名字加 id：名字是叫法，id 是账本上的编号，两个都要。
$alice = Post-J '/api/driver/prompt' @{ id = 'alice' }
Check '同席列表用名字打头' ($alice.text -match '露比（ruby）') 'name（id）'
Check '自己那一行不再挂 id' ($ruby.text -notmatch 'You are 露比 \(id') '自我介绍里没有 id'

Write-Host '=== 8. 她眼里的别人，是她自己写的'
Post-J '/api/round/begin' $null | Out-Null
Post-J '/api/round/order' @{
    actorId = 'alice'; kind = 'speak'; targetId = 'ruby'
    text = '你好'; impression = '一位长得非常漂亮的猫人小姐，说话却很直'
} | Out-Null
Post-J '/api/round/resolve' $null | Out-Null

$seen = Post-J '/api/driver/prompt' @{ id = 'alice' }
Check '写下的印象进了自己的 prompt' ($seen.text -match '说话却很直') '有那句'
Check '这一段说清了这是判断不是事实' ($seen.text -match 'your judgement, not a fact') '有分界'

$other = Post-J '/api/driver/prompt' @{ id = 'bob' }
Check '别人看不到我的印象' ($other.text -notmatch '说话却很直') '只属于写下它的那个人'

Write-Host '=== 9. 印象落在盘上'
Post-J '/api/save' $null | Out-Null
# CLARRY_DATA 指的是放世界存档的那个目录，不是存档本身。早先这里把它当文件用了，
# 本地没设这个环境变量所以一路走到默认路径、看不出问题；CI 里设了，于是它拿目录去
# Get-Content，报的是「Unable to get content because it is a directory」。
$dataDir = if ($env:CLARRY_DATA) { $env:CLARRY_DATA } else { Join-Path (Split-Path -Parent $PSScriptRoot) 'data' }
$worldFile = Join-Path $dataDir 'world.json'
if (Test-Path -LiteralPath $worldFile) {
    $json = Get-Content -LiteralPath $worldFile -Raw -Encoding UTF8
    Check '存档里写下了这句印象' ($json -match '说话却很直') '在盘上'
} else {
    Write-Host ("     找不到存档 {0}，跳过这条" -f $worldFile) -ForegroundColor DarkGray
}

Write-Host '=== 10. 三层认知并排看'
$who = Get-J '/api/who-is/ruby'
Check '她自己的说法在' ($who.selfImage -match '非常漂亮') $who.selfImage
Check 'alice 对她的印象在' (@($who.impressions | Where-Object { $_.byId -eq 'alice' }).Count -eq 1) ("{0} 个人写过，{1} 个人本可以写" -f $who.heardFrom, $who.couldHaveHeard)
Check '两层并排，谁也不校正谁' ((@($who.impressions | Where-Object { $_.text -match '说话却很直' }).Count -eq 1) -and ($who.selfImage -match '非常漂亮')) '她说的和别人说的都留着'
Check '没人写过的人不出现' (@($who.impressions | Where-Object { $_.byId -eq 'bob' }).Count -eq 0) '没看过就是没看过'

Write-Host ''
if ($fails -eq 0) { Write-Host '全部检查通过' -ForegroundColor Green }
else { Write-Host ("{0} 项检查未通过" -f $fails) -ForegroundColor Red; exit 1 }
