# plan-check.ps1 - 工程地图自检：JSON 读得动，且每个目标写下的路径真的在盘上。
#
# design/PLAN.json 是目标网络，不是变更日志。它只有两个字段真正会出错：
#   touches     这个目标碰哪些文件或目录
#   verifiedBy  哪套验收来判它
# 两者都可能因为改名、移动、删除而指向一个已经不存在的东西，而 JSON 本身不会报错——
# 地图看起来还在，影响面却已经空了。这个脚本就是把那种沉默的失效找出来。
#
# 用法：
#   & scripts\plan-check.ps1              检查（默认取脚本上一级作为工程根）
#   & scripts\plan-check.ps1 -Root <路径>  指定别的工程
#
# 退出码 0 = 通过，1 = 有问题。问题逐条列出，不汇总成一句「有错」。

param([string]$Root = (Split-Path $PSScriptRoot -Parent))

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$planPath = Join-Path $Root 'design\PLAN.json'
if (-not (Test-Path -LiteralPath $planPath)) { throw "找不到工程地图：$planPath" }

try { $plan = Get-Content -LiteralPath $planPath -Raw -Encoding UTF8 | ConvertFrom-Json }
catch { throw "工程地图不是合法 JSON：$($_.Exception.Message)" }

if (-not $plan.goals) { throw '工程地图里没有 goals' }

$problems = New-Object System.Collections.Generic.List[string]
$ids = @{}
$statuses = @('done', 'todo', 'doing', 'blocked', 'open')

foreach ($g in $plan.goals) {
    if (-not $g.id) { $problems.Add('有一个目标没有 id'); continue }
    if ($ids.ContainsKey($g.id)) { $problems.Add("id 重复：$($g.id)") }
    $ids[$g.id] = $true
    if (-not $g.title) { $problems.Add("$($g.id) 缺少 title") }
    if ($g.status -notin $statuses) { $problems.Add("$($g.id) 的 status 不在允许值内：$($g.status)") }

    foreach ($t in @($g.touches)) {
        if (-not $t) { continue }
        $p = Join-Path $Root ($t -replace '/', '\')
        if (-not (Test-Path -LiteralPath $p)) { $problems.Add("$($g.id) 指向不存在的路径：$t") }
    }
    foreach ($v in @($g.verifiedBy)) {
        if (-not $v) { continue }
        $p = Join-Path $Root ($v -replace '/', '\')
        if (-not (Test-Path -LiteralPath $p)) { $problems.Add("$($g.id) 的验收不存在：$v") }
    }
}

# after 边指向一个不在表里的目标，等于这条依赖被读丢了。
foreach ($g in $plan.goals) {
    foreach ($a in @($g.after)) {
        if ($a -and -not $ids.ContainsKey($a)) { $problems.Add("$($g.id) 的 after 指向不存在的目标：$a") }
    }
}

$done = @($plan.goals | Where-Object { $_.status -eq 'done' }).Count
$todo = @($plan.goals | Where-Object { $_.status -eq 'todo' }).Count
$touchCount = ($plan.goals | ForEach-Object { @($_.touches).Count } | Measure-Object -Sum).Sum
$testCount = ($plan.goals | ForEach-Object { @($_.verifiedBy).Count } | Measure-Object -Sum).Sum

[Console]::WriteLine(('{0} 个目标：{1} 完成 / {2} 待办，{3} 条文件边，{4} 条验收边' -f `
    $plan.goals.Count, $done, $todo, $touchCount, $testCount))

if ($problems.Count -eq 0) {
    [Console]::WriteLine('自检通过：每条路径都在盘上，每条 after 都指向表里的目标。')
    exit 0
}

[Console]::WriteLine("发现 $($problems.Count) 处问题：")
foreach ($p in $problems) { [Console]::WriteLine("  $p") }
exit 1
