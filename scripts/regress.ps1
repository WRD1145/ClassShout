# 一键跑完整回归：局域网链路 + 中继链路。
#
# 中继服务器用一个全新的临时状态目录和环境变量启动，因此不会碰到
# 上一次测试留在程序目录里的 relay-*.json，也不会污染真实部署。
#
#   pwsh -File scripts/regress.ps1
#   pwsh -File scripts/regress.ps1 -Port 8098
#
# 退出码 0 表示两个链路全部通过；非 0 表示有失败项（详情看输出）。
[CmdletBinding()]
param(
    [int]$Port = 8097,
    [string]$Configuration = 'Release',
    [switch]$Tts
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$e2e = Join-Path $root "tools\ClassShout.EndToEnd\bin\$Configuration\net10.0-windows\ClassShout.EndToEnd.exe"
$srv = Join-Path $root "src\ClassShout.RelayServer\bin\$Configuration\net10.0\ClassShout.RelayServer.exe"

foreach ($path in @($e2e, $srv)) {
    if (-not (Test-Path $path)) {
        throw "找不到 $path，请先执行：dotnet build ClassShout.slnx -c $Configuration"
    }
}

# 先构建再测。
#
# 这一步不是可有可无的礼节：中继服务器是独立进程，如果忘了重建，上面那两个
# 可执行文件还是上一次的，于是"全部通过"测的是旧代码 —— 而你会以为新改动没问题。
# 这个坑真的踩过一次，所以让脚本自己保证被测二进制是最新的。
Write-Host "构建中（$Configuration）…" -ForegroundColor Yellow
& dotnet build (Join-Path $root 'ClassShout.DesktopOnly.slnf') -c $Configuration -v q --nologo
if ($LASTEXITCODE -ne 0) {
    throw "构建失败（退出码 $LASTEXITCODE），先修好再跑回归。"
}

$state = Join-Path ([System.IO.Path]::GetTempPath()) ('cs-regress-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $state -Force | Out-Null
Write-Host "状态目录：$state" -ForegroundColor Cyan

$env:CLASSSHOUT_CONFIG = Join-Path $state 'relay-config.json'
$env:CLASSSHOUT_USER_STATE = Join-Path $state 'relay-users.json'
$env:CLASSSHOUT_RELAY_STATE = Join-Path $state 'relay-state.json'
$env:CLASSSHOUT_BINDING_STATE = Join-Path $state 'relay-bindings.json'
$env:CLASSSHOUT_SCHEDULE_STATE = Join-Path $state 'relay-schedule.json'
$env:CLASSSHOUT_SCHEDULE_AUDIO = Join-Path $state 'relay-schedule-audio'

# 日志也放进这个临时目录：真实部署里这条路径由启动脚本设成"软件目录\logs"，
# 而下面那一段要断言的正是"日志确实写到了这个变量指的目录里"，不是别处。
$env:CLASSSHOUT_LOG_DIR = Join-Path $state 'logs'
New-Item -ItemType Directory -Path $env:CLASSSHOUT_LOG_DIR -Force | Out-Null

# 按天分文件 + 只留 7 天这两件事，光看代码不算数：这里在服务器启动前先种两份
# 名字里带日期的旧日志，等它启动（第一次写日志时会清理一次）之后再回来看结果。
$expiredLog = Join-Path $env:CLASSSHOUT_LOG_DIR ('classshout-' + (Get-Date).AddDays(-8).ToString('yyyy-MM-dd') + '.log')
$keptLog = Join-Path $env:CLASSSHOUT_LOG_DIR ('classshout-' + (Get-Date).AddDays(-6).ToString('yyyy-MM-dd') + '.log')
Set-Content -Path $expiredLog -Value '8 天前的那一份' -Encoding utf8NoBOM
Set-Content -Path $keptLog -Value '6 天前的那一份' -Encoding utf8NoBOM

# 服务器定时默认每 5 秒扫一次，而自检要真的等到"到点发出去"这件事发生 ——
# 压到 200 毫秒，一个用例才不用干等五秒。生产上没必要更密：定时精确到分钟。
$env:CLASSSHOUT_SCHEDULE_TICK_MS = '200'

$log = Join-Path $state 'server.log'
$proc = Start-Process -FilePath $srv -ArgumentList '--urls', "http://127.0.0.1:$Port" `
    -RedirectStandardOutput $log -RedirectStandardError "$log.err" -PassThru -WindowStyle Hidden

try {
    $ready = $false
    for ($i = 0; $i -lt 80; $i++) {
        Start-Sleep -Milliseconds 400
        if ($proc.HasExited) { break }
        try {
            $health = Invoke-RestMethod "http://127.0.0.1:$Port/api/health" -TimeoutSec 2
            if ($health.ok) { $ready = $true; break }
        }
        catch { }
    }

    if (-not $ready) {
        Write-Host '中继服务器未能就绪，日志末尾：' -ForegroundColor Red
        Get-Content $log, "$log.err" -ErrorAction SilentlyContinue | Select-Object -Last 40
        exit 1
    }

    # 口令必须从状态目录读，而不是从某个固定的本地路径 ——
    # 服务器的工作目录由上面的环境变量决定，真实部署里它可能在完全不同的机器上。
    $adminPassword = (Get-Content $env:CLASSSHOUT_CONFIG -Raw | ConvertFrom-Json).AdminPassword
    Write-Host "中继服务器就绪（管理员口令已从配置文件读出，$($adminPassword.Length) 位）" -ForegroundColor Cyan

    Write-Host ''
    Write-Host '================ 局域网链路 ================' -ForegroundColor Yellow
    $lanArgs = @()
    if ($Tts) { $lanArgs += '--tts' }
    & $e2e @lanArgs
    $lanExit = $LASTEXITCODE

    Write-Host ''
    Write-Host '================ 中继链路 ================' -ForegroundColor Yellow
    & $e2e --relay "http://127.0.0.1:$Port" --admin-password $adminPassword
    $relayExit = $LASTEXITCODE

    Write-Host ''
    Write-Host '================ 服务端文件日志 ================' -ForegroundColor Yellow

    # 这一段守的是"日志文件里到底有没有东西"。
    #
    # 起因是一个真实发生过的 bug：落盘的过滤器按"类别名是不是以 ClassShout 开头"
    # 判断，而服务器自己的主日志类别叫 Relay —— 于是登录、授权、喊话、定时发送
    # 这些最该留档的 Information 全被挡在文件之外，日志文件里只剩启动那几行。
    # 启动那几行看着挺正常，所以光"文件建出来了"根本发现不了，
    # 必须断言到具体的事件行上。
    $logDir = Join-Path $state 'logs'
    $todayName = 'classshout-' + (Get-Date).ToString('yyyy-MM-dd') + '.log'
    $todayLog = Join-Path $logDir $todayName
    $logText = if (Test-Path $todayLog) { Get-Content $todayLog -Raw } else { '' }
    $logFailures = 0

    foreach ($check in @(
        @{ Name = '日志写在 CLASSSHOUT_LOG_DIR 指的目录里'; Ok = (Test-Path $todayLog); Detail = $todayLog }
        @{ Name = '文件按天命名'; Ok = ($todayName -match '^classshout-\d{4}-\d{2}-\d{2}\.log$'); Detail = $todayName }
        @{ Name = '启动过程有记录'; Ok = ($logText -match '定时任务调度已启动'); Detail = '' }
        @{ Name = '启动信息（类别叫 Relay 的那一份）也进了文件'; Ok = ($logText -match '注册表：'); Detail = '状态目录是全新的，所以这里出现的是「已有 0 条记录」那一条' }
        @{ Name = '运行事件（管理员登录）也进了文件'; Ok = ($logText -match '管理员登录成功'); Detail = '这一条曾经被类别过滤器整批挡掉' }
        @{ Name = '老师从网页喊话有记录'; Ok = ($logText -match '从网页'); Detail = '' }
        @{ Name = '日志里没有管理员口令明文'; Ok = (-not $logText.Contains($adminPassword)); Detail = '' }
        @{ Name = '超过 7 天的日志被清掉'; Ok = (-not (Test-Path $expiredLog)); Detail = (Split-Path -Leaf $expiredLog) }
        @{ Name = '保留期内的日志不动'; Ok = (Test-Path $keptLog); Detail = (Split-Path -Leaf $keptLog) }
    )) {
        if ($check.Ok) {
            $suffix = if ($check.Detail) { " —— $($check.Detail)" } else { '' }
            Write-Host "  [通过] $($check.Name)$suffix"
        }
        else {
            $suffix = if ($check.Detail) { " —— $($check.Detail)" } else { '' }
            Write-Host "  [失败] $($check.Name)$suffix" -ForegroundColor Red
            $logFailures++
        }
    }

    if ($logFailures -gt 0 -and -not (Test-Path $todayLog)) {
        Write-Host "  日志目录里现有：$((Get-ChildItem $logDir -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Name) -join '、')" -ForegroundColor DarkGray
    }
}
finally {
    if (-not $proc.HasExited) {
        $proc.Kill()
        $proc.WaitForExit(5000) | Out-Null
    }

    Remove-Item -Recurse -Force $state -ErrorAction SilentlyContinue
}

Write-Host ''
Write-Host '================ 教室端托盘驻留 ================' -ForegroundColor Yellow
& (Join-Path $PSScriptRoot 'smoke-tray.ps1') -ErrorAction Continue
$trayExit = $LASTEXITCODE

Write-Host ''
Write-Host '================ 教室端单实例 ================' -ForegroundColor Yellow
& (Join-Path $PSScriptRoot 'smoke-single-instance.ps1') -ErrorAction Continue
$singleExit = $LASTEXITCODE

Write-Host ''
Write-Host '================ 控制台前端转义 ================' -ForegroundColor Yellow

# 用 Node 把真正的 app.js 跑起来断言转义行为，所以它守的是浏览器会执行的那份代码，
# 而不是一份照抄过来的副本。没有 Node 就跳过 —— 这是唯一一个非 .NET 的测试。
$webuiExit = 0
if (Get-Command node -ErrorAction SilentlyContinue) {
    & node (Join-Path $PSScriptRoot 'smoke-webui.mjs')
    $webuiExit = $LASTEXITCODE
}
else {
    Write-Host '  跳过 —— 未找到 node' -ForegroundColor Yellow
}

if ($null -eq $logFailures) { $logFailures = 0 }

Write-Host ''
Write-Host "局域网退出码：$lanExit    中继退出码：$relayExit    托盘退出码：$trayExit    单实例退出码：$singleExit    前端退出码：$webuiExit    日志失败项：$logFailures" -ForegroundColor Cyan

if ($lanExit -ne 0 -or $relayExit -ne 0 -or $trayExit -ne 0 -or $singleExit -ne 0 -or $webuiExit -ne 0 -or $logFailures -ne 0) {
    Write-Host '存在失败项。' -ForegroundColor Red
    exit 1
}

Write-Host '全部通过。' -ForegroundColor Green
