# test-loop.ps1 - 让剧场自己转起来：挂上脚本驱动，跑几轮，再读回结算结果。
# 在接任何模型之前，先把回合机制本身验一遍。

$base = 'http://127.0.0.1:3090'

$kindLabel = @{
    speak = '说话'; act = '行动'; probe = '试探'
    give = '给予'; reveal = '揭露'; withhold = '沉默'; game = '小游戏'
}

function Get-J([string]$path) {
    Invoke-RestMethod -Uri "$base$path" -Method Get -TimeoutSec 20
}

function Post-J([string]$path, $body) {
    if ($null -eq $body) {
        Invoke-RestMethod -Uri "$base$path" -Method Post -TimeoutSec 20
    } else {
        Invoke-RestMethod -Uri "$base$path" -Method Post -TimeoutSec 30 `
            -ContentType 'application/json' -Body ($body | ConvertTo-Json -Depth 8)
    }
}

Write-Host '=== 开场前的状态' -ForegroundColor Cyan
Get-J '/api/health' | ConvertTo-Json -Depth 6

Write-Host '=== 挂上脚本驱动' -ForegroundColor Cyan
foreach ($id in @('alice', 'bob', 'carol')) {
    Post-J '/api/driver/attach' @{ id = $id; mode = 'scripted' } | ConvertTo-Json -Depth 6
}

Write-Host '=== 起循环：3 轮，间隔 200 毫秒' -ForegroundColor Cyan
$snap = Post-J '/api/loop/start' @{ intervalMs = 200; maxRounds = 3 }
Write-Host ("运行中={0} 轮次={1} 受驱使={2}" -f $snap.running, $snap.round, $snap.drivenSeats)

Start-Sleep -Seconds 6

Write-Host '=== 循环状态' -ForegroundColor Cyan
$st = Get-J '/api/loop/status'
Write-Host ("运行中={0} 轮次={1} 收场原因={2}" -f $st.running, $st.round, $st.stopReason)
Write-Host '--- 日志尾部'
$st.log | Select-Object -Last 18 | ForEach-Object { '  ' + $_.text }

Write-Host '=== 结算后的世界' -ForegroundColor Cyan
Get-J '/api/world' | ConvertTo-Json -Depth 6

Write-Host '=== 历史尾部' -ForegroundColor Cyan
(Get-J '/api/history?take=12') | ForEach-Object {
    $label = if ($kindLabel.ContainsKey($_.kind)) { $kindLabel[$_.kind] } else { $_.kind }
    '  {0,-6} {1,-6} {2}' -f $_.actorId, $label, $_.text
}

Write-Host '=== 卸下驱动并收场' -ForegroundColor Cyan
Post-J '/api/loop/stop' $null | Out-Null
foreach ($id in @('alice', 'bob', 'carol')) {
    Post-J '/api/driver/detach' @{ id = $id } | Out-Null
}
Get-J '/api/health' | ConvertTo-Json -Depth 6
