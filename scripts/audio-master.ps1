# audio-master.ps1 - 统一出口：裁掉首尾静音、按规格归一响度、转成发行格式。
#
# 它不做创作，只保证出去的每一段音频都守同一条规格。三个来源（谱子渲染、DAW 导出、TTS）
# 若不在出口处统一，进游戏之后就会出现「过场很响、日常很轻」这类靠音量条解决不了的问题。
#
# 响度做两遍：先测量再应用。一遍动态归一化会把音乐压成一条直线，游戏音乐不该被那样对待。

param(
    [Parameter(Mandatory)][string]$InDir,
    [Parameter(Mandatory)][string]$OutDir,
    [ValidateSet('bgm', 'se', 'voice')][string]$Kind = 'bgm',
    [string]$Ffmpeg = 'D:\ffmpeg-8.1.1-essentials_build\bin\ffmpeg.exe',
    [int]$Quality = 6,
    [switch]$DryRun
)

$ErrorActionPreference = 'Continue'

if (-not (Test-Path $Ffmpeg)) { throw "找不到 ffmpeg：$Ffmpeg" }
if (-not (Test-Path $InDir))  { throw "找不到输入目录：$InDir" }
if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }

# 规格：见 docs/AUDIO.md
$targetLufs = switch ($Kind) { 'bgm' { -16.0 } 'se' { -18.0 } 'voice' { -16.0 } }
$truePeak = -1.5
$sampleRate = 48000

Write-Host ("=== 类别 {0}：目标 {1} LUFS，真峰值 {2} dBTP，{3} Hz" -f $Kind, $targetLufs, $truePeak, $sampleRate)
Write-Host ("    输入 {0}" -f $InDir)
Write-Host ("    输出 {0}" -f $OutDir)

$sources = Get-ChildItem $InDir -File -Include *.wav, *.flac, *.aiff -Recurse -ErrorAction SilentlyContinue
if ($sources.Count -eq 0) { Write-Host '  没有找到可处理的音频文件'; exit 0 }

function Measure-Loudness([string]$file) {
    $filter = "loudnorm=I=${targetLufs}:TP=${truePeak}:LRA=11:print_format=json"
    $raw = & $Ffmpeg -hide_banner -nostats -i $file -af $filter -f null - 2>&1 | Out-String
    $m = [regex]::Match($raw, '(?s)\{[^{}]*"input_i"[^{}]*\}')
    if (-not $m.Success) { return $null }
    try { return $m.Value | ConvertFrom-Json } catch { return $null }
}

$done = 0
$failed = 0
$skipped = 0

foreach ($src in $sources) {
    $name = [IO.Path]::GetFileNameWithoutExtension($src.Name)
    $out = Join-Path $OutDir ($name + '.ogg')

    Write-Host ("--- {0}  ({1:0.0} MB)" -f $src.Name, ($src.Length / 1MB))

    $measured = Measure-Loudness $src.FullName
    if ($null -eq $measured) {
        Write-Host '    量不出来，跳过（可能是空音轨或格式不支持）'
        $skipped++
        continue
    }

    Write-Host ("    原始响度 {0} LUFS，真峰值 {1} dBTP，动态范围 {2}" -f `
        $measured.input_i, $measured.input_tp, $measured.input_lra)

    if ($DryRun) {
        Write-Host '    （预演，不转码）'
        $skipped++
        continue
    }

    # 裁剪首尾静音 → 按测量值线性归一化 → 转 OGG
    $trim = 'silenceremove=start_periods=1:start_threshold=-50dB:start_silence=0.1'
    $trimRev = 'silenceremove=start_periods=1:start_threshold=-50dB:start_silence=0.1'
    $norm = "loudnorm=I=${targetLufs}:TP=${truePeak}:LRA=11:measured_I=$($measured.input_i):measured_LRA=$($measured.input_lra):measured_TP=$($measured.input_tp):measured_thresh=$($measured.input_thresh):linear=true"
    $chain = "$trim,areverse,$trimRev,areverse,$norm"

    $args = @(
        '-hide_banner', '-nostats', '-y',
        '-i', $src.FullName,
        '-af', $chain,
        '-ar', "$sampleRate",
        '-c:a', 'libvorbis', '-q:a', "$Quality",
        $out
    )

    & $Ffmpeg @args 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $out)) {
        Write-Host ("    转码失败（ffmpeg 退出码 {0}）" -f $LASTEXITCODE)
        $failed++
        continue
    }

    # $out 是 Join-Path 出来的字符串，不是 FileInfo —— 这里只能传它本身，
    # 传 $out.FullName 会得到一个空值，然后拿空路径去量，测出 null。
    $after = Measure-Loudness $out
    $sizeKb = (Get-Item $out).Length / 1KB
    if ($null -ne $after) {
        Write-Host ("    产出 {0}  出口响度 {1} LUFS  {2:0.0} KB" -f (Split-Path $out -Leaf), $after.input_i, $sizeKb)
    } else {
        Write-Host ("    产出 {0}  {1:0.0} KB" -f (Split-Path $out -Leaf), $sizeKb)
    }
    $done++
}

Write-Host ''
Write-Host ("完成 {0} 个，失败 {1} 个，跳过 {2} 个" -f $done, $failed, $skipped)
if ($failed -gt 0) { exit 1 }
