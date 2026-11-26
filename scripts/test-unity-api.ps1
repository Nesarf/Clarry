# unity-api-check.ps1 - Unity 客户端抄的那份 DTO，与真实 HTTP 响应对不对得上。
#
# 这套不需要 Unity 运行，也不需要编辑器授权：字段名与类型从 ClarryApi.cs 里读，另一边拿真实的
# /api/cast 与 /api/history 对账。判据不自己抄一份字段表，否则判据本身也会漂。
#
# 为什么需要它：Unity 这边的 DTO 是手抄的。Clarry.Core 用 C# 13（required、file 作用域
# 命名空间），Unity 2022.3 停在 C# 9，共享模型做不到，于是字段名与类型成了一份靠人记的约定。
# 约定不报错就不报错 —— 服务端改个键名，客户端只会安静地显示空字符串；类型对不上时
# JsonUtility 跳过那个字段，读出来永远是 0 或 false，同样安静。
#
# 用法：
#   & scripts\unity-api-check.ps1
#   & scripts\unity-api-check.ps1 -Base http://127.0.0.1:3091
#
# 服务要先起来：& rebuild.ps1 -Start
#
# 跑位次：这套要排在 test-glimpse 之前。那一套会 wipe 世界、只留自己摆的两个人，
# 而那两个人的立绘路径指着 assets 里不存在的图 —— 在它之后跑，这里会报一堆真的但不是
# 这套要管的事。

param(
    [string]$Base = 'http://127.0.0.1:3090'
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [System.Text.Encoding]::UTF8

$root = Split-Path -Parent $PSScriptRoot
$apiPath = Join-Path $root 'unity\ClarryStage\Assets\Scripts\ClarryApi.cs'

$script:fails = @()
$script:checks = 0
$script:notes = @()

function Check([string]$name, [scriptblock]$body) {
    $script:checks++
    try {
        $ok = & $body
        if ($ok) {
            Write-Host ("  通过  {0}" -f $name) -ForegroundColor Green
        } else {
            Write-Host ("  失败  {0}" -f $name) -ForegroundColor Red
            $script:fails += $name
        }
    } catch {
        Write-Host ("  失败  {0}（{1}）" -f $name, $_.Exception.Message) -ForegroundColor Red
        $script:fails += $name
    }
}

# 从一个 .cs 里抓某个 class 的字段：名字到类型。只认 `public <类型> <名字>;`。
function FieldsOf([string]$source, [string]$className) {
    $m = [regex]::Match($source, "class\s+$className\s*\{(?<body>.*?)\r?\n\s*\}", 'Singleline')
    $map = [ordered]@{}
    if (-not $m.Success) { return $map }
    foreach ($f in [regex]::Matches($m.Groups['body'].Value, 'public\s+(?<type>[A-Za-z0-9_\.\[\]]+)\s+(?<name>[A-Za-z0-9_]+)\s*;')) {
        $map[$f.Groups['name'].Value] = $f.Groups['type'].Value
    }
    return $map
}

# JsonUtility 只认这几个类型。判据拿 PowerShell 解出来的 .NET 类型去对。
function TypeFits([string]$want, $value) {
    if ($null -eq $value) { return $true }   # 空值由「非空」那几条单独判
    $t = $value.GetType()
    switch ($want) {
        'int'     { return $t -eq [int] -or $t -eq [long] -or $t -eq [double] -or $t -eq [decimal] }
        'long'    { return $t -eq [long] -or $t -eq [int] }
        'float'   { return $t -eq [double] -or $t -eq [single] -or $t -eq [int] -or $t -eq [long] }
        'double'  { return $t -eq [double] -or $t -eq [int] -or $t -eq [long] }
        'bool'    { return $t -eq [bool] }
        'string'  { return $t -eq [string] }
        'string[]' { return $t.IsArray -and $t.GetElementType() -eq [string] }
        default   { return $t -eq [System.Management.Automation.PSCustomObject] }
    }
}

# Invoke-RestMethod 在 PS 7 里把顶层数组当成一个整体交出来，用 @() 包会把整个数组变成
# 一个元素（于是 Count 是 1、元素是 Object[]）。这里按类型分开处理，别再用 @() 包它。
function GetArray([string]$path) {
    $raw = Invoke-RestMethod -Uri ($Base + $path) -Method Get -TimeoutSec 15
    if ($null -eq $raw) { return , @() }
    if ($raw -is [array]) { return , $raw }
    return , @($raw)
}

Write-Host '=== Unity 客户端与 HTTP 面'

if (-not (Test-Path -LiteralPath $apiPath)) {
    Write-Host ("  失败  找不到 {0}" -f $apiPath) -ForegroundColor Red
    Write-Host '1 项检查未通过'
    exit 1
}

$src = Get-Content -LiteralPath $apiPath -Raw
$castDto = FieldsOf $src 'CastMember'
$turnDto = FieldsOf $src 'Turn'
$attrDto = FieldsOf $src 'Attributes'

Check 'ClarryApi.cs 里读得到三个 DTO 的字段' {
    $castDto.Count -gt 0 -and $turnDto.Count -gt 0 -and $attrDto.Count -gt 0
}

# BaseUrl 是写死的常量。脚本换了端口时，这一条立刻指出两边不是一个世界。
$declared = [regex]::Match($src, 'BaseUrl\s*=\s*"([^"]+)"').Groups[1].Value
Check ("BaseUrl 与这套在打的地址一致（{0}）" -f $declared) {
    $declared.TrimEnd('/') -eq $Base.TrimEnd('/')
}

$cast = GetArray '/api/cast'
Check '台上有至少一个人（这套需要夹具，先跑 test-all 或手动建人）' { $cast.Count -gt 0 }

if ($cast.Count -gt 0) {
    # StageView 真正读的五个字段。缺一个，那一格就少一样东西。
    $hard = @('id', 'name', 'role', 'face', 'faces')

    foreach ($f in $hard) {
        Check ("CastMember.{0} 每个成员上都有" -f $f) {
            @($cast | Where-Object { -not ($_.PSObject.Properties.Name -contains $f) }).Count -eq 0
        }
    }

    Check 'id / name / role 都不是空的（残缺角色会让 Unity 画出空白卡）' {
        $empty = @($cast | Where-Object {
                [string]::IsNullOrWhiteSpace([string]$_.id) -or
                [string]::IsNullOrWhiteSpace([string]$_.name) -or
                [string]::IsNullOrWhiteSpace([string]$_.role)
            })
        if ($empty.Count -gt 0) { $script:notes += "残缺成员：" + (($empty | ForEach-Object { "[$($_.id)]" }) -join '、') }
        $empty.Count -eq 0
    }

    # 抄了就该有用得上的一天；全都没有说明 DTO 与服务端已经分家。
    foreach ($f in @($castDto.Keys | Where-Object { $hard -notcontains $_ })) {
        Check ("CastMember.{0} 至少在一个人身上出现过" -f $f) {
            @($cast | Where-Object { $_.PSObject.Properties.Name -contains $f }).Count -gt 0
        }
    }

    # 类型：int 抄成 bool、对象抄成 int，JsonUtility 都会静默跳过，读出来永远是默认值。
    foreach ($f in $castDto.Keys) {
        if ($hard -contains $f) { continue }
        $want = $castDto[$f]
        Check ("CastMember.{0} 的类型对得上（DTO 写的是 {1}）" -f $f, $want) {
            $bad = @()
            foreach ($m in $cast) {
                if (-not ($m.PSObject.Properties.Name -contains $f)) { continue }
                if (-not (TypeFits $want $m.$f)) {
                    $bad += ("{0}={1}" -f $m.id, $m.$f.GetType().Name)
                    break
                }
            }
            if ($bad.Count -gt 0) { $script:notes += ("{0} 类型不符：{1}" -f $f, ($bad -join '、')) }
            $bad.Count -eq 0
        }
    }

    Check 'faces 是数组，且每个人至少一条' {
        @($cast | Where-Object { -not ($_.faces -is [array]) -or $_.faces.Count -eq 0 }).Count -eq 0
    }

    # 候选链里天生有画不存在的名字：ShotRules 只按 id 与枚举算，从不看盘（这一层不摸文件是
    # 故意的，否则同一份存档在两台机器上会给出不同的候选）。最后一项是 {id}.png，那是「这个人
    # 至少能有一张画」的命名约定。它不存在时客户端会把整条链试完、然后留白，所以这里只把
    # 「谁会留白」摆出来，不当失败 —— 那是一个 checkout 的事实，不是一个 bug。
    Check '候选链最后一项是 {id}.png（基础立绘的命名约定）' {
        @($cast | Where-Object { $_.faces[-1] -notmatch ('/' + [regex]::Escape($_.id) + '\.png$') }).Count -eq 0
    }

    $haveBase = @()
    $noBase = @()
    foreach ($m in $cast) {
        $rel = ($m.faces[-1] -replace '^/assets/', '')
        if (Test-Path -LiteralPath (Join-Path $root "assets/$rel")) { $haveBase += $m } else { $noBase += $m }
    }

    if ($noBase.Count -gt 0) {
        $script:notes += '没有基础立绘、舞台上会留白的人：' + (($noBase | ForEach-Object { $_.id }) -join '、')
    }

    # 静态文件那一段真的在发图吗 —— 用有立绘的人验，别用规则链里那些可能不存在的名字。
    if ($haveBase.Count -gt 0) {
        Check ("有基础立绘的 {0} 个人，那张图都下载得到且是图片" -f $haveBase.Count) {
            $bad = @()
            foreach ($m in $haveBase) {
                try {
                    $r = Invoke-WebRequest -Uri ($Base + $m.faces[-1]) -Method Get -TimeoutSec 20 -UseBasicParsing
                    $ct = [string]$r.Headers['Content-Type']
                    if ($r.StatusCode -ne 200 -or $ct -notmatch '^image/') { $bad += "$($m.id)（$($r.StatusCode) $ct）" }
                } catch {
                    $bad += "$($m.id)（取不到）"
                }
            }
            if ($bad.Count -gt 0) { $script:notes += '取不到的立绘：' + ($bad -join '；') }
            $bad.Count -eq 0
        }
    }

    # attributes：DTO 声明的必须在，类型也得对。响应里多出来的键只记备注 —— JsonUtility
    # 忽略未知字段是正常的，而它支持的形状比 JSON 窄（没有字典）。
    $withAttr = @($cast | Where-Object { $_.PSObject.Properties.Name -contains 'a' -and $null -ne $_.a })
    if ($withAttr.Count -gt 0) {
        $wire = @($withAttr[0].a.PSObject.Properties.Name)

        Check 'Attributes 里 DTO 声明的字段，响应里都有' {
            $gone = @($attrDto.Keys | Where-Object { $wire -notcontains $_ })
            if ($gone.Count -gt 0) { $script:notes += "DTO 有、响应没给：" + ($gone -join '、') }
            $gone.Count -eq 0
        }

        Check 'Attributes 的字段类型对得上' {
            $bad = @()
            foreach ($f in $attrDto.Keys) {
                if ($wire -notcontains $f) { continue }
                if (-not (TypeFits $attrDto[$f] $withAttr[0].a.$f)) {
                    $bad += ("{0}: DTO {1} / 响应 {2}" -f $f, $attrDto[$f], $withAttr[0].a.$f.GetType().Name)
                }
            }
            if ($bad.Count -gt 0) { $script:notes += ($bad -join '；') }
            $bad.Count -eq 0
        }

        $extra = @($wire | Where-Object { -not $attrDto.Contains($_) })
        if ($extra.Count -gt 0) {
            $script:notes += "响应有、DTO 没抄（JsonUtility 会忽略，属正常）：" + ($extra -join '、')
        }
    }
}

$history = GetArray '/api/history?take=10'

if ($history.Count -gt 0) {
    # StageView 读的是 actorId、text、addressedTo 三个。
    foreach ($f in @('actorId', 'kind', 'text')) {
        Check ("Turn.{0} 在每条历史里都有，且是字符串" -f $f) {
            @($history | Where-Object {
                    -not ($_.PSObject.Properties.Name -contains $f) -or -not (TypeFits 'string' $_.$f)
                }).Count -eq 0
        }
    }

    # 旁白在 StageView 里靠这个值缩进两格；值变了，那一段就变成「世界：」而不是缩进。
    if (@($history | Where-Object { $_.actorId -eq '世界' }).Count -eq 0) {
        $script:notes += '这一段历史里没有旁白（actorId 为「世界」），StageView 那条缩进分支这次没被验到'
    }
} else {
    $script:notes += '历史是空的：Turn 那三条这次没有样本可对'
}

Write-Host ''
if ($script:notes.Count -gt 0) {
    foreach ($n in $script:notes) { Write-Host ("  备注  {0}" -f $n) -ForegroundColor DarkGray }
    Write-Host ''
}

if ($script:fails.Count -gt 0) {
    Write-Host ("{0} 项检查未通过" -f $script:fails.Count) -ForegroundColor Red
    exit 1
}

Write-Host ("全部检查通过（{0} 项）" -f $script:checks) -ForegroundColor Green
exit 0
