# check-art.ps1 - 清点立绘：每个角色一套要 12 张，现在有几张、缺哪几张。
# 不依赖服务：角色名就是 portraits 目录里那些不带 - 与 _ 的 png 文件名。

param(
    [string]$Portraits = 'D:\Clarry\assets\portraits'
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

if (-not (Test-Path $Portraits)) {
    Write-Host ("没有立绘目录：" + $Portraits) -ForegroundColor Red
    exit 1
}

# 一次读目录，之后只查表；几百张图逐个 Test-Path 会慢得没必要。
$have = @{}
foreach ($f in (Get-ChildItem $Portraits -Filter '*.png' -File)) { $have[$f.Name] = $f.Length }

# 角色名 = 没有 - 也没有 _ 的那批 png（alice.png 是角色，alice-closeup.png 是它的差分）。
$ids = @($have.Keys | Where-Object { $_ -notmatch '[-_]' } | ForEach-Object { [IO.Path]::GetFileNameWithoutExtension($_) } | Sort-Object)

if ($ids.Count -eq 0) {
    Write-Host '没有找到任何角色的基础立绘（形如 alice.png）。' -ForegroundColor Yellow
    exit 0
}

$faces = @('happy', 'unhappy', 'surprised', 'ember')
$lines = @('neutral') + $faces
$shots = @('closeup', 'memory')

# 一张人一套：基础、四张表情、两个镜头、五张线稿。顺序就是候选链从粗到细的顺序。
function Wanted([string]$id) {
    $out = @()
    foreach ($s in $shots) { $out += "$id-$s.png" }
    foreach ($e in $lines) { $out += "$id-line-$e.png" }
    foreach ($e in $faces) { $out += "$id-$e.png" }
    $out += "$id.png"
    return $out
}

$totalWanted = 0
$totalHave = 0
$hardMissing = @()

foreach ($id in $ids) {
    $wanted = Wanted $id
    $found = @($wanted | Where-Object { $have.ContainsKey($_) })
    $missing = @($wanted | Where-Object { -not $have.ContainsKey($_) })

    $totalWanted += $wanted.Count
    $totalHave += $found.Count

    $pct = [math]::Round(100 * $found.Count / $wanted.Count)
    $color = if ($pct -eq 100) { 'Green' } elseif ($pct -ge 50) { 'Yellow' } else { 'Gray' }
    Write-Host ("  {0,-10} {1,2}/{2}  {3,3}%" -f $id, $found.Count, $wanted.Count, $pct) -ForegroundColor $color

    if (-not $have.ContainsKey("$id.png")) { $hardMissing += $id }

    foreach ($m in $missing) { Write-Host ("     缺 {0}" -f $m) -ForegroundColor DarkGray }
}

Write-Host ''
$pct = if ($totalWanted -eq 0) { 0 } else { [math]::Round(100 * $totalHave / $totalWanted) }
Write-Host ("汇总：{0} 个角色，{1} 张需求，{2} 张就位（{3}%）。" -f $ids.Count, $totalWanted, $totalHave, $pct)

if ($hardMissing.Count -gt 0) {
    Write-Host ("硬伤：这些角色连基础立绘都没有 —— " + ($hardMissing -join '、')) -ForegroundColor Red
    exit 1
}

Write-Host '每个人的基础立绘都在，缺的都是差分；缺哪张就退回上一档，舞台上不会出现空洞。' -ForegroundColor Green
