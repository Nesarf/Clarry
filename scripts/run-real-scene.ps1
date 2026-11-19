# run-real-scene.ps1 - 给三个席位写真实的角色卡，接上真正的 OpenAI 兼容端点，
# 然后让剧场在无人干预下自己演一场。
#
# key 在运行时从环境变量读，不写进这个文件。

param(
    [string]$Endpoint = 'https://api.deepseek.com/v1',
    [string]$Model = 'deepseek-chat',
    [int]$Rounds = 4,
    [int]$IntervalMs = 400
)

$base = 'http://127.0.0.1:3090'

$kindLabel = @{
    speak = '说话'; act = '行动'; probe = '试探'
    give = '给予'; reveal = '揭露'; withhold = '沉默'; game = '小游戏'
}

function Get-J([string]$p) { Invoke-RestMethod -Uri "$base$p" -Method Get -TimeoutSec 30 }
function Post-J([string]$p, $b) {
    if ($null -eq $b) {
        Invoke-RestMethod -Uri "$base$p" -Method Post -TimeoutSec 60
    } else {
        Invoke-RestMethod -Uri "$base$p" -Method Post -TimeoutSec 120 `
            -ContentType 'application/json' -Body ($b | ConvertTo-Json -Depth 12)
    }
}

$key = $env:DEEPSEEK_API_KEY
if ([string]::IsNullOrWhiteSpace($key)) {
    throw '这个进程看不到 DEEPSEEK_API_KEY，挂不上真驱动。'
}

# --- 演员表 -----------------------------------------------------------------
# 角色卡才是一个模型变成一个人的原因。只有 Role 的话，你得到的是三个客气的陌生人；
# 加上 summary、voice、想什么、怕什么，和一件捂着的事，才有戏。

$cast = @(
    @{
        id = 'alice'; name = '爱丽丝'
        sheet = @{
            role    = '剑士'
            summary = '被逐出骑士团的年轻剑士。剑比话快，但总在出手前犹豫一瞬。'
            voice   = '短句，少用敬语；说到自己不擅长的话题会停半拍。'
            traits  = @('直率', '护短', '不擅长说谎')
            goals   = @('拿回被夺走的家名')
            fears   = @('被当成一把好用的工具')
            secrets = @('她随身那把剑是别人给的，不是家传的。')
        }
    },
    @{
        id = 'bob'; name = '鲍勃'
        sheet = @{
            role    = '学者'
            summary = '把一切都记进册子的中年学者。怕的不是死，是记错。'
            voice   = '爱用「据我观察」开头，句子偏长，会自己打断自己。'
            traits  = @('谨慎', '好奇', '迂腐')
            goals   = @('补全那本没人读完的《残页考》')
            fears   = @('自己的记录被证明是假的')
            secrets = @('他烧掉过一页，因为那页上写着他的名字。')
        }
    },
    @{
        id = 'carol'; name = '卡萝尔'
        sheet = @{
            role    = '吟游诗人'
            summary = '专唱别人不愿被唱到的事。笑得轻，听得清。'
            voice   = '半唱半说，喜欢把话绕一圈再落到点上。'
            traits  = @('敏锐', '促狭', '心软')
            goals   = @('找一首能让自已哭出来的歌')
            fears   = @('台下再没有人在听')
            secrets = @('她唱的每一首都是真事，只是换了名字。')
        }
    }
)

Write-Host '=== 开一场新戏' -ForegroundColor Cyan
Post-J '/api/scene/new?clueGoal=3' $null | Out-Null
Write-Host '  历史、回合账与线索进度已清空，演员保留'

Write-Host '=== 重写三张角色卡' -ForegroundColor Cyan
foreach ($c in $cast) {
    $body = @{
        id      = $c.id
        name    = $c.name
        isPlayer = $false
        sheet   = $c.sheet
        visual  = @{ kind = 'pixel'; ref = "/assets/portraits/$($c.id).png"; expressions = @() }
        agent   = @{ kind = 'llm'; autonomy = 'guided' }
    }
    Post-J '/api/character' $body | Out-Null
    Write-Host ("  {0}（{1}）" -f $c.name, $c.sheet.role)
}

Write-Host '=== 让他们上台' -ForegroundColor Cyan
foreach ($c in $cast) { Post-J '/api/stage/enter' @{ id = $c.id } | Out-Null }
(Get-J '/api/world').stage -join '、'

Write-Host '=== 给每人挂一个真驱动' -ForegroundColor Cyan
foreach ($c in $cast) {
    $r = Post-J '/api/driver/attach' @{
        id          = $c.id
        mode        = 'http-llm'
        endpoint    = $Endpoint
        model       = $Model
        apiKey      = $key
        temperature = 1.0
        maxTokens   = 300
        timeoutMs   = 180000
    }
    Write-Host ("  {0} -> {1}" -f $c.id, $r.kind)
}

Write-Host ("=== 让它自己跑：最多 {0} 轮，间隔 {1} 毫秒" -f $Rounds, $IntervalMs) -ForegroundColor Cyan
Post-J '/api/loop/start' @{ intervalMs = $IntervalMs; maxRounds = $Rounds; userWaitMs = 30000 } | Out-Null

# 等它自己收场
$deadline = (Get-Date).AddMinutes(6)
do {
    Start-Sleep -Seconds 2
    $st = Get-J '/api/loop/status'
} while ($st.running -and (Get-Date) -lt $deadline)

Write-Host '=== 循环日志' -ForegroundColor Cyan
$st.log | ForEach-Object { '  ' + $_.text }

Write-Host '=== 这出戏' -ForegroundColor Cyan
(Get-J '/api/history?take=40') |
    Where-Object { $_.kind -in @('speak', 'act', 'probe', 'give', 'withhold', 'reveal') } |
    ForEach-Object {
        $to = if ($_.addressedTo) { ' → ' + $_.addressedTo } else { '' }
        $label = if ($kindLabel.ContainsKey($_.kind)) { $kindLabel[$_.kind] } else { $_.kind }
        '{0,-8} {1,-6}{2} {3}' -f $_.actorId, $label, $to, $_.text
    }

Write-Host '=== 之后的状态' -ForegroundColor Cyan
Get-J '/api/world' | ConvertTo-Json -Depth 6
