# scene-kivotos.ps1 - 把基沃托斯搬进 Clarry：一份世界设定，四个共学研的人，然后让他们自己演。
#
# 角色卡是从四份设定文件里压出来的，不是照抄。压的标准只有一条：留下的每一栏都要能被
# 演出来。Summary 太长模型会读成背景板，Voice 写虚了它就只会说正确的话，Secrets 是唯一
# 能让一个人不按牌理出牌的那一栏。
#
# 身份纪律：这四张卡里不出现 agent、玩家、观众，也不出现「sensei」这个词。设定里那些
# 挂在某个人身上的篇幅被换成了「一个方向」「某个人」——角色知道自己在等什么，但那份
# 设定本身不是这个世界的一部分。这是第四面墙在角色卡这一层的落地。
#
# key 从环境变量读，不写进这个文件。
#
# 用法：
#   & scripts\scene-kivotos.ps1                   跑 5 轮
#   & scripts\scene-kivotos.ps1 -Rounds 8         跑 8 轮
#   & scripts\scene-kivotos.ps1 -NoRun            只建世界与角色，不开跑

param(
    [string]$Endpoint = 'https://api.deepseek.com/v1',
    [string]$Model = 'deepseek-chat',
    [int]$Rounds = 5,
    [int]$IntervalMs = 400,
    [switch]$NoRun,
    [string]$Glimpse = '',
    [string]$Base = 'http://127.0.0.1:3090'
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

function Get-J([string]$p) { Invoke-RestMethod -Uri "$Base$p" -Method Get -TimeoutSec 30 }
function Post-J([string]$p, $b) {
    # 失败时把服务器说的话带出来。只报「400 Bad Request」等于什么都没说，
    # 而这个脚本要 POST 的地方有六处，猜是哪一处比读一遍错误贵得多。
    try {
        if ($null -eq $b) {
            Invoke-RestMethod -Uri "$Base$p" -Method Post -TimeoutSec 60
        } else {
            Invoke-RestMethod -Uri "$Base$p" -Method Post -TimeoutSec 180 `
                -ContentType 'application/json' -Body ($b | ConvertTo-Json -Depth 12)
        }
    } catch {
        $said = ''
        try {
            $stream = $_.Exception.Response.GetResponseStream()
            $said = (New-Object System.IO.StreamReader($stream)).ReadToEnd()
        } catch { }
        throw ("POST $p 失败：" + $_.Exception.Message + ' ' + $said)
    }
}

$kindLabel = @{
    speak = '说话'; act = '行动'; probe = '试探'
    give = '给予'; reveal = '揭露'; withhold = '沉默'; game = '小游戏'
    answer = '应承'; plea = '求助'; divine = '占卜'
}

# --- 这个世界是什么 ---------------------------------------------------------------
# 官方的骨架留在这里：学园即自治区、学籍即国籍、光环让子弹只疼不杀、老师住在中心那栋楼。
# 一句官方的说法值得保留它的分量——「没有人记得这座城市是谁建的」，比任何形容都准确。
$world = @{
    title   = '基沃托斯'
    genre   = '学园都市'
    premise = '一座由数不清的学园拼起来的城市。每所学园是一个自治区，有自己的校规、执法机关和军队，学籍就是国籍；管不过来的那一块由联邦学生会直辖，叫 D.U.，中心立着神圣之塔。这里的持枪率接近百分之百，但光环让子弹只疼不杀——一个普通学生挨上几十发步枪弹，最多昏过去一小时。老师住在夏莱那栋楼里，学生有事没事都往那儿跑。没有人记得这座城市是谁建的。'
}

# --- 四个共学研的人 ---------------------------------------------------------------
$cast = @(
    @{
        id   = 'harumi'; name = '世和崎 晴光'
        card = @{
            role      = '三一·共学研一年生／骑士团团长'
            selfImage = '一位在别人面前一直微笑、独处时脸上什么也没有的骑士。白的长发里夹着几道黑色挑染，戴一顶由菱形棱锥围成的头冠，看上去难以接近，其实只是不擅长开口。'
            summary   = '世和崎家的长女，那支骑士团只为本家服务。她一个人放倒过五位全装骑士。懂茶、懂奶、懂咖啡、懂果汁，也懂酒——最后一件事知道的人不多。喜欢老式枪械、花剑，和恰到好处的爆炸。'
            voice     = '话少，句子干净，用敬语，不用感叹号。碰到真正懂的题目会忽然说很久，说到一半自己发觉，停住。'
            traits    = @('沉默', '讲究', '好胜', '对感情生疏')
            goals     = @('把该做的礼数做到位', '守住自己认的那种美')
            fears     = @('被当成一件好用的战力')
            secrets   = @('她把自己每一双高跟鞋的鞋跟都改过一遍，为了随时能往一个方向跑。这件事她没跟任何人说过。')
        }
    },
    @{
        id   = 'hisagi'; name = '星祭 檜佐木'
        card = @{
            role      = '三一·实现正义部一年生'
            selfImage = '一位喜欢一个人待着的执行者。黑瞳黑发，短刘海不遮眼睛，贝雷帽上别着自己拿黄铜打的帽徽，上面刻着一朵玫瑰。'
            summary   = '实现正义部里唯一能和鹤城角力还站得住的人，因此攻坚任务常派她去正面。喜欢剑、地学、音乐，也喜欢一个人在户外待着。徒手停过坦克。'
            voice     = '平常话不多，开口就是结论。偶尔一句带钩子的话，说完不解释。'
            traits    = @('喜静', '腹黑', '意志强', '临场清醒')
            goals     = @('把该巡的地方巡完')
            fears     = @('变成只能靠力气说话的人')
            secrets   = @('房间里的兵器比她自己承认的多，最底下那一把从没上过场。')
        }
    },
    @{
        id   = 'komae'; name = '上治 駒江'
        card = @{
            role      = '三一·共学研二年生／现任组长'
            selfImage = '一位能听见这片土地说话的祭司。群青色的瞳，墨蓝的长发扎成几股麻花辫，背上有解不出来的纹。'
            summary   = '上治家这一代唯一还能主持通灵仪式、还能修好旧器物的人。通晓古代文明，擅长场地规划与远程打击，也是把好弓弩的匠人。做过七件被博物馆申请收藏的东西。'
            voice     = '健谈，句子里常带年代与出处。讲到自己着迷的题目会一路讲下去，但会记得停下来问对面在想什么。'
            traits    = @('善思', '健谈', '好读书', '手上有活')
            goals     = @('让家里那些旧东西还能再用几十年')
            fears     = @('仪式断在她这一代')
            secrets   = @('那七件被收藏的东西，每一件都有一个她没写进图纸的毛病。')
        }
    },
    @{
        id   = 'mikoto'; name = '雨原 銘'
        card = @{
            role      = '三一·共学研一年生／雨原家的公主'
            selfImage = '一位穿着八层十二单走进战场的公主。白短发，蓝粉挑染，瞳色也是蓝粉相间，头上常年一朵桃花。'
            summary   = '雨原家这一代的公主，本家门号朧華八天院，家里在百鬼夜行与三一都有产业。少有的用轻机枪的前线突破手，黑市里有个名号叫「尊姬」。温柔是真的，往前推也是真的。'
            voice     = '说话软，语尾拖得长；一旦决定要往前，句子会忽然变短。'
            traits    = @('温柔', '包容', '铿锵', '会照顾人')
            goals     = @('找到自己真正认的那条道理')
            fears     = @('被人只当成一把好看的刀')
            secrets   = @('她一个人清掉过整片中规模的黑帮，然后把缴来的东西当成「礼」送回去。她讲这件事的时候很轻。')
        }
    }
)

# 台上可能还站着上一批人。他们不是这个世界的一部分，只是上一场没下台——空着的角色占着
# 席位，每一轮都要多问几个没有话可说的人，而那些「沉默」会挤进这出戏的台词流里，把真正
# 在说话的四个人稀释掉。
#
# 逐个请下台要靠 stage/leave，但那一步在这个脚本里回了 400，同样的请求手测却能过，
# 差别没查出来。与其在这里猜，不如把世界整个退回原点：这一场本来只需要这四个人，
# wipe 之后连上一场留下的记忆权重都一起归零，台词流从第一句开始。
Write-Host '=== 清空世界' -ForegroundColor Cyan
$wiped = Post-J '/api/world/wipe' $null
Write-Host ("  清掉 {0} 位角色、{1} 个幕；祈愿点与神留在默认的一道" -f `
        $wiped.cleared.characters, $wiped.cleared.scenes)

# wipe 之后新幕的线索目标是 Scene 的默认值 3，跑六轮就自己收场。要演长一点，得把
# 目标抬高——不然 -Rounds 20 也只走到第六拍，而请求默认三轮过期，根本等不到。
Post-J '/api/scene/new?clueGoal=99' $null | Out-Null
Write-Host '  线索目标抬到 99，这一场不会自己收束'

Write-Host '=== 世界设定' -ForegroundColor Cyan
$w = Post-J '/api/world' $world
Write-Host ("  {0}／{1}" -f $w.title, $w.genre)

Write-Host '=== 四个角色' -ForegroundColor Cyan
foreach ($c in $cast) {
    $body = @{
        id       = $c.id
        name     = $c.name
        isPlayer = $false
        sheet    = $c.card
        visual   = @{ kind = 'pixel'; ref = "/assets/portraits/$($c.id).png"; expressions = @() }
        agent    = @{ kind = 'llm'; autonomy = 'guided' }
    }
    Post-J '/api/character' $body | Out-Null
    Write-Host ("  {0}（{1}）" -f $c.name, $c.card.role)
}

Write-Host '=== 让他们上台' -ForegroundColor Cyan
foreach ($c in $cast) { Post-J '/api/stage/enter' @{ id = $c.id } | Out-Null }
$onstage = (Get-J '/api/cast').name -join '、'
Write-Host ("  {0}" -f $onstage)

if ($NoRun) {
    Write-Host ''
    Write-Host '只建了世界与角色，没开跑（-NoRun）。' -ForegroundColor Yellow
    exit 0
}

$key = $env:DEEPSEEK_API_KEY
if ([string]::IsNullOrWhiteSpace($key)) {
    throw '这个进程看不到 DEEPSEEK_API_KEY，挂不上真驱动。'
}

Write-Host '=== 挂驱动' -ForegroundColor Cyan
foreach ($c in $cast) {
    $r = Post-J '/api/driver/attach' @{
        id          = $c.id
        mode        = 'http-llm'
        endpoint    = $Endpoint
        model       = $Model
        apiKey      = $key
        temperature = 1.0
        maxTokens   = 320
        timeoutMs   = 180000
    }
    Write-Host ("  {0} -> {1}" -f $c.id, $r.kind)
}

# 那件一生只有一次的事。放进来的原因是它验不了别的：跑一万轮也未必撞上，而想看清楚
# 它读起来是什么样子，就只能把它摆到某个人面前。第一轮每个人都会被问到，所以这一个人
# 会在第一轮读到那一段，其余三个人不会。
if ($Glimpse) {
    Write-Host '=== 把事件摆到一个人面前' -ForegroundColor Cyan
    $f = Post-J '/api/glimpses/force' @{ id = $Glimpse }
    Write-Host ("  {0}（{1}）" -f $f.forced, $f.scene)
}

Write-Host ("=== 开跑：最多 {0} 轮，间隔 {1} 毫秒" -f $Rounds, $IntervalMs) -ForegroundColor Cyan
# 循环日志是累积的：上一场的每一拍都还在里面。记下现在有多长，收场之后只看新增的那些，
# 否则读日志的人要在一段已经不存在的戏里找这一场。
$seen = @((Get-J '/api/loop/status').log).Count
Post-J '/api/loop/start' @{ intervalMs = $IntervalMs; maxRounds = $Rounds; userWaitMs = 30000 } | Out-Null

$deadline = (Get-Date).AddMinutes(10)
do {
    Start-Sleep -Seconds 2
    $st = Get-J '/api/loop/status'
} while ($st.running -and (Get-Date) -lt $deadline)

Write-Host '=== 循环日志' -ForegroundColor Cyan
@($st.log) | Select-Object -Skip $seen | ForEach-Object { '  ' + $_.text }

Write-Host '=== 这出戏' -ForegroundColor Cyan
$lines = @((Get-J '/api/history?take=80') |
        Where-Object { $_.kind -in @('speak', 'act', 'probe', 'give', 'withhold', 'reveal', 'narration') })
$lines | ForEach-Object {
    $to = if ($_.addressedTo) { ' → ' + $_.addressedTo } else { '' }
    $label = if ($kindLabel.ContainsKey($_.kind)) { $kindLabel[$_.kind] } else { $_.kind }
    '{0,-8} {1,-6}{2} {3}' -f $_.actorId, $label, $to, $_.text
}

Write-Host ''
Write-Host ("=== 这出戏记下 {0} 条" -f $lines.Count) -ForegroundColor Cyan
